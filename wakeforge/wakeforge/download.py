"""Model & OpenSLR acoustic downloads; independently cached, no hidden downloads during tests."""
from __future__ import annotations
import os, shutil, subprocess, sys, urllib.request, zipfile, time
from pathlib import Path

SOURCES={'slr26':('https://openslr.elda.org/resources/26/sim_rir_16k.zip','https://www.openslr.org/resources/26/sim_rir_16k.zip'),
         'slr28':('https://openslr.elda.org/resources/28/rirs_noises.zip','https://www.openslr.org/resources/28/rirs_noises.zip')}

def fetch_archive(url,dest):
    dest.parent.mkdir(parents=True,exist_ok=True)
    partial=dest.with_suffix(dest.suffix+'.part')
    have=partial.stat().st_size if partial.exists() else 0
    req=urllib.request.Request(url,headers={'User-Agent':'WakeForge/0.2',**({'Range':f'bytes={have}-'} if have else {})})
    with urllib.request.urlopen(req,timeout=150) as resp:
        resumed=bool(have and resp.status==206)
        mode='ab' if resumed else 'wb'
        initial=have if resumed else 0
        remaining=int(resp.headers.get('Content-Length') or 0)
        total=remaining+initial if remaining else 0
        copied=initial
        begun=last=time.monotonic()
        print(f'DOWNLOAD {dest.name}: {total/1024**2:.1f} MiB' if total else f'DOWNLOAD {dest.name}: size unknown', flush=True)
        with partial.open(mode) as file:
            while True:
                chunk=resp.read(2*1024*1024)
                if not chunk:break
                file.write(chunk)
                copied+=len(chunk)
                now=time.monotonic()
                if now-last>=2 or (total and copied>=total):
                    rate=(copied-initial)/max(.001,now-begun)
                    remain=(total-copied)/rate if total and rate else 0
                    cells=int(24*copied/total) if total else 0
                    state=f'[{"#"*cells}{"."*(24-cells)}] {copied/total:6.1%}' if total else f'{copied/1024**2:.1f} MiB'
                    print(f'{state} DOWNLOAD {dest.name} {rate/1024**2:.1f} MiB/s ETA {int(remain)}s',flush=True)
                    last=now
    if not zipfile.is_zipfile(partial):raise RuntimeError(f'Bad ZIP {url}: {partial}')
    os.replace(partial,dest)

def fetch_large_tar(urls, dest):
    """Resumable multi-gigabyte download, with mirrors and no bulk unpack."""
    import tarfile
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.is_file():
        try:
            with tarfile.open(dest, mode='r|*') as archive:
                if next(iter(archive), None) is not None:
                    return dest
        except (OSError, tarfile.TarError):
            print('Golos cache is invalid; download will be retried', flush=True)
            dest.unlink()
    partial=dest.with_suffix(dest.suffix+'.part')
    failures=[]
    for url in urls:
        try:
            have=partial.stat().st_size if partial.exists() else 0
            request=urllib.request.Request(url,headers={
                'User-Agent':'WakeForge/0.6',
                **({'Range':f'bytes={have}-'} if have else {})})
            with urllib.request.urlopen(request,timeout=180) as response:
                append=(have>0 and response.status==206)
                # A mirror that ignores Range cannot be appended to existing data.
                if response.status==206 and not append:
                    raise RuntimeError('Unexpected partial HTTP response')
                initial=have if append else 0
                size=int(response.headers.get('Content-Length') or 0)
                total=size+initial if size else 0
                print(f'GOLOS download: {total/1024**3:.2f} GiB (resume: {initial/1024**3:.2f} GiB)',flush=True)
                start=time.monotonic();last=start;written=initial
                with partial.open('ab' if append else 'wb') as target:
                    while True:
                        chunk=response.read(2*1024*1024)
                        if not chunk:break
                        target.write(chunk);written+=len(chunk)
                        now=time.monotonic()
                        if now-last>2 or (total and written>=total):
                            rate=(written-initial)/max(.001,now-start)
                            eta=int((total-written)/rate) if rate and total else -1
                            print(f'GOLOS {written/1024**3:.2f} GiB '
                                  f'{written/total:.1%} ' if total else f'GOLOS {written/1024**3:.2f} GiB ',
                                  f'{rate/1024**2:.1f} MiB/s ETA {eta if eta>=0 else "?"}s',flush=True)
                            last=now
                if total and written != total:
                    raise RuntimeError(f'Incomplete download: {written}/{total}')
            with tarfile.open(partial,mode='r|*') as archive:
                if next(iter(archive),None) is None:
                    raise RuntimeError('Golos TAR archive is empty')
            os.replace(partial,dest)
            return dest
        except Exception as exc:
            failures.append(f'{url}: {exc}')
            print(f'Golos mirror failed: {exc}',flush=True)
    raise RuntimeError('Не удалось скачать Golos. '+ ' | '.join(failures))


def extract_audio(zip_path,target):
    marker=target/'.wakeforge_extracted'
    if marker.exists():return
    with zipfile.ZipFile(zip_path) as ar:
        files=[i for i in ar.infolist() if i.filename.lower().endswith('.wav')]
        for i,entry in enumerate(files,1):
            rel=Path(entry.filename)
            if rel.is_absolute() or '..' in rel.parts:raise RuntimeError(f'Unsafe archive path {rel}')
            out=target/rel;out.parent.mkdir(parents=True,exist_ok=True)
            if out.is_file() and out.stat().st_size==entry.file_size:continue
            temp=out.with_name(out.name+'.part')
            with ar.open(entry) as source,temp.open('wb') as dest:shutil.copyfileobj(source,dest)
            os.replace(temp,out)
            if i%100==0 or i==len(files):
                cells=int(24*i/len(files)) if files else 24
                print(f'[{"#"*cells}{"."*(24-cells)}] {i/len(files):6.1%} '
                      f'EXTRACT {zip_path.name}: {i:,}/{len(files):,}',flush=True)
    marker.write_text(str(zip_path),encoding='utf-8')

def download(paths,cfg,acoustics=True):
    from .core import save_json
    from huggingface_hub import snapshot_download
    print('Qwen TTS model:',cfg['tts_model'],flush=True)
    folder=snapshot_download(cfg['tts_model'],cache_dir=str(paths['cache']/'hf'))
    print('Model cached:',folder,flush=True)
    from .voices import list_voices
    human=list_voices(paths)
    cloned_folder=None
    if human:
        clone_model=cfg.get('clone_model','Qwen/Qwen3-TTS-12Hz-1.7B-Base')
        print('Base model for real-voice cloning:',clone_model,flush=True)
        cloned_folder=snapshot_download(clone_model,cache_dir=str(paths['cache']/'hf'))
        print('Cloning model cached:',cloned_folder,flush=True)

    # Both acoustic packs are REQUIRED. Old projects may contain a stale toggle.
    for label,urls in SOURCES.items():
        archive=paths['acoustics']/(label+'.zip')
        if not archive.exists():
            for url in urls:
                try:
                    print('OpenSLR:',url,flush=True);fetch_archive(url,archive)
                    break
                except Exception as exc:print(f'Mirror unavailable: {exc}',flush=True)
            if not archive.exists():raise RuntimeError(f'No OpenSLR mirror worked for {label}')
        extract_audio(archive,paths['acoustics']/label)
    from .golos import GOLOS_URLS, prepare_golos, assert_ready
    official=paths['cache']/'golos'/'golos_opus.tar.gz'
    fetch_large_tar(GOLOS_URLS,official)
    prepare_golos(paths,cfg,archive=official)
    assert_ready(paths,cfg)
    # Commit only after every required source is downloaded, decoded and checked.
    # This prevents the UI from treating a failed stage as complete.
    marker={'tts_model':cfg['tts_model'], 'tts_cache':str(folder),
            'clone_model':cfg.get('clone_model') if human else None,
            'clone_cache':str(cloned_folder) if cloned_folder else None,
            'acoustics':True, 'golos':True}
    save_json(paths['cache']/'downloads.json',marker)
    print('DOWNLOAD COMPLETE',flush=True)
