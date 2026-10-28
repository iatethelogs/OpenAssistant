"""WakeForge: open-source Russian wake-word model builder CLI."""
from __future__ import annotations
import argparse,json,os,shutil,subprocess,sys
from pathlib import Path
from .core import suggested_hard,default_config,detect,project_paths,project_config,save_json,validate,slug


def launch(module,options,tail=None):
    cmd=[sys.executable,'-u','-m','wakeforge.legacy.'+module,*options,*(tail or [])]
    print('RUN:', ' '.join(map(str,cmd)),flush=True)
    env=os.environ.copy()
    # TensorFlow and the mmap loader can open many feature shards.
    if os.name=='posix':
        try:
            import resource
            soft,hard=resource.getrlimit(resource.RLIMIT_NOFILE)
            resource.setrlimit(resource.RLIMIT_NOFILE,(min(max(soft,65535),hard),hard))
        except Exception:pass
    result=subprocess.run(cmd,env=env)
    if result.returncode:raise RuntimeError(f'Stage {module} failed with exit {result.returncode}')


def init_cmd(a):
    hw=detect()
    name=a.name or input('Желаемое имя активации: ').strip()
    if not name:raise ValueError('Пустое имя')
    hard=(a.hard.split(',') if a.hard else [])
    if not hard and sys.stdin.isatty():
        print('Похожие отрицательные слова через запятую. Enter для встроенного списка.')
        value=input('Негативы: ').strip();hard=value.split(',') if value else suggested_hard(name)
    if not hard:hard=suggested_hard(name)
    work=Path(a.project or ('./projects/'+slug(name))).expanduser().resolve()
    if (work/'project.json').exists():raise FileExistsError(f'Проект уже существует: {work}')
    cfg=default_config(name,hard=hard,voices=a.voices,variants=a.variants,hardware=hw)
    if a.tts_gpus is not None:
        cfg['voice_gpus']=[int(x.strip()) for x in a.tts_gpus.split(',') if x.strip()] if a.tts_gpus.lower()!='cpu' else []
    if a.train_gpu is not None:cfg['train_gpu']=a.train_gpu
    if a.workers is not None:cfg['workers']=a.workers
    validate(cfg)
    work.mkdir(parents=True,exist_ok=True);save_json(work/'project.json',cfg)
    print('\nPROJECT:',work,'\nCONFIG:',work/'project.json',flush=True)
    print('Hardware:',json.dumps(hw,ensure_ascii=False),flush=True)
    print('Stages: setup -> download -> generate -> audit -> augment -> challenge -> features -> train -> evaluate -> export')
    print('Run all: wakeforge all --project',work)


def doctor(a,paths,cfg,hw):
    print('TARGET:',cfg['keyword'])
    print('NEGATIVE WORDS:',', '.join(cfg['negative_words']))
    print('CPU workers:',cfg['workers'],'| Visible GPU:',hw['gpus'])
    print('TTS assigned GPU:',cfg['voice_gpus'],'| Single training GPU:',cfg['train_gpu'])
    modules={'numpy':'numpy','soundfile':'soundfile','sounddevice':'sounddevice','scipy':'scipy','torch':'torch',
             'qwen_tts':'qwen-tts','faster_whisper':'faster-whisper',
             'pymicro_features':'pymicro-features','mmap_ninja':'mmap-ninja',
             'microwakeword':'microwakeword','tensorflow':'tensorflow','yaml':'pyyaml'}
    import importlib.util
    missing=[]
    for module,pkg in modules.items():
        found=importlib.util.find_spec(module) is not None
        print(' OK ' if found else 'MISS',pkg)
        if not found:missing.append(pkg)
    print('Missing:',', '.join(missing) if missing else 'none')
    print('Disk:',round(shutil.disk_usage(paths['root'].parent).free/1024**3,2),'GiB free')
    from .golos import assert_ready
    try: print('GOLOS:',assert_ready(paths,cfg)['created'],'real speech clips')
    except (RuntimeError, FileNotFoundError):print('GOLOS: missing, run download')


def pip_setup(a):
    if not a.confirm:
        print('This installs packages in CURRENT Python environment (no venv). Add --yes to approve.')
        return
    hw=detect()
    packages=['numpy>=2.0','scipy>=1.13','soundfile>=0.12','sounddevice>=0.5.2','pyyaml>=6',
              'pymicro-features','mmap-ninja','qwen-tts>=0.1.1','huggingface_hub>=0.28',
              'faster-whisper>=1.2.1', ('tensorflow[and-cuda]>=2.18' if hw['tts_gpus'] and sys.platform=='linux' else 'tensorflow>=2.18'),
              'git+https://github.com/kahrendt/microWakeWord.git']
    if hw['tts_gpus'] and sys.platform=='linux': packages += ['nvidia-cublas-cu12','nvidia-cudnn-cu12']
    cmd=[sys.executable,'-m','pip','install',*packages]
    if a.break_system:cmd.insert(4,'--break-system-packages')
    print('INSTALL:', ' '.join(cmd));subprocess.run(cmd,check=True)
    print('Installed. GPU dependencies (driver/Torch/CUDA/cuDNN) may require platform setup.')


def aug_cmd(paths,cfg,extra=None):
    from .core import SPLITS
    def count(split):
        p=paths['positive']/f'positive_{split}.jsonl'
        if not p.exists():raise FileNotFoundError(p)
        return sum(bool(line.strip()) for line in p.open(encoding='utf-8'))
    from .golos import assert_ready
    assert_ready(paths,cfg)
    args=['--word',cfg['keyword'], '--positive',str(paths['positive']),
          '--negative',str(paths['negative']),'--acoustics',str(paths['acoustics']),
          '--out',str(paths['augmented']),'--workers',str(cfg['workers']),
          '--shard-size',str(min(512,cfg['shard_size']))]
    for split in SPLITS:
        n=count(split)
        for category,mult in [('augclean','augclean_multiplier'),('hardmix','hardmix_multiplier')]:
            args+=['--'+category+'-'+split,str(n*cfg[mult])]
    launch('augment',args,extra)


def fit_cmd(action,paths,cfg,extra=None):
    from .golos import assert_ready
    assert_ready(paths,cfg)
    args=[action,'--keyword',cfg['keyword'],'--positive',str(paths['positive']),
          '--augmented',str(paths['augmented']),'--negative',str(paths['negative']),
          '--work',str(paths['training']),'--workers',str(cfg['workers']),
          '--shard-size',str(cfg['shard_size']), '--steps',str(cfg['steps']),
          '--batch',str(cfg['batch']),'--eval-interval',str(cfg['eval_interval']),
          '--gpu',str(cfg['train_gpu'])]
    launch('fit',args,extra)
    if action=='train':
        # Only a fully successful training command receives a completion marker.
        import hashlib
        plan=paths['training']/'training_plan.json'
        if not plan.is_file():
            raise RuntimeError('Training plan absent after the training command')
        signature=hashlib.sha256(plan.read_bytes()).hexdigest()
        (paths['training']/'.wakeforge_training.done').write_text(signature,encoding='utf-8')


def evaluate_cmd(paths,cfg,extra=None):
    from .golos import assert_ready
    assert_ready(paths,cfg)
    args=['--runs',str(paths['training']/'runs'), '--work',str(paths['training']),
          '--positive',str(paths['positive']), '--augmented',str(paths['augmented']),
          '--negative',str(paths['negative']), '--out',str(paths['training']/'evaluation'),
          '--parallel','1']
    launch('evaluate',['val',*args],extra)
    launch('evaluate',['test',*args],extra)


def export_cmd(paths,cfg):
    found=list((paths['training']/'runs').glob('gpu*_wakeword/model/tflite_stream_state_internal_quant/stream_state_internal_quant.tflite'))
    if len(found)!=1:raise RuntimeError(f'Expected single final .tflite, found {len(found)}: {found}')
    paths['models'].mkdir(parents=True,exist_ok=True)
    target=paths['models']/(slug(cfg['keyword'])+'.tflite');shutil.copy2(found[0],target)
    report=paths['training']/'evaluation'/'report_test.json'
    metadata={'keyword':cfg['keyword'],'architecture':'mixednet','sample_rate':16000,
              'frontend':'pymicro_features MicroFrontend 40 bins 10ms',
              'stride':3,'tflite':target.name,'source':str(found[0]),
              'evaluation':json.loads(report.read_text(encoding='utf-8')) if report.is_file() else None}
    save_json(paths['models']/'model.json',metadata)
    print('MODEL:',target,'bytes:',target.stat().st_size,flush=True)


def status(paths,cfg):
    def counted(p):
        return sum(1 for _ in p.open()) if p.is_file() else 0
    print('PROJECT:',paths['root'])
    print('NAME:',cfg['keyword'])
    print('SYNTHETIC VOICE PROFILES:',cfg['voice_profiles'])
    from .voices import list_voices
    print('RECORDED HUMAN VOICES:',len(list_voices(paths)))
    for split in ('train','val','test'):
        for group,folder in [('clean',paths['positive']),('augclean',paths['augmented']),('hardmix',paths['augmented'])]:
            name=f'positive_{split}.jsonl' if group=='clean' else f'positive_{group}_{split}.jsonl'
            print(f'  {split}/{group}: {counted(folder/name)}')
    print('MODELS:',[str(x) for x in paths['models'].glob('*.tflite')])


def main(argv=None):
    if argv is None:argv=sys.argv[1:]
    # A single `wakeforge` command opens the human-facing interactive workflow.
    # Keep explicit subcommands available for scripting, CI and headless servers.
    if not argv or argv == ['menu']:
        from .menu import main_menu
        return main_menu()
    if argv and argv[0]=='_tts_worker':
        inner=argparse.ArgumentParser(prog='wakeforge _tts_worker')
        inner.add_argument('--project',required=True);inner.add_argument('--device',required=True)
        inner.add_argument('--part',type=int,required=True);inner.add_argument('--total',type=int,required=True)
        inner.add_argument('--mode',choices=['design','clone'],default='design')
        inner_args=inner.parse_args(argv[1:])
        from .tts import worker
        sys.exit(worker(inner_args.project,inner_args.device,inner_args.part,inner_args.total,inner_args.mode))
    parser=argparse.ArgumentParser(prog='wakeforge',description='End-to-end wake-word builder | no Docker, no virtualenv required')
    sub=parser.add_subparsers(dest='action',required=True)
    ini=sub.add_parser('init',help='Interactive new project')
    ini.add_argument('--name');ini.add_argument('--hard',help='Comma-separated phonetic confusables')
    ini.add_argument('--project');ini.add_argument('--voices',type=int)
    ini.add_argument('--variants',type=int);ini.add_argument('--tts-gpus',help='0,1,... or cpu')
    ini.add_argument('--train-gpu',type=int);ini.add_argument('--workers',type=int)
    setup=sub.add_parser('setup',help='Install dependencies into current Python (--yes required)')
    setup.add_argument('--yes',dest='confirm',action='store_true')
    setup.add_argument('--break-system-packages',dest='break_system',action='store_true')
    for name in ('doctor','record','export-voices','import-voices','download','generate','audit','augment','challenge','features','train','evaluate','export','status','all'):
        p=sub.add_parser(name)
        p.add_argument('--project',required=True)
        if name=='record':p.add_argument('--name',help='Имя диктора')
        if name in ('export-voices','import-voices'):p.add_argument('--file',type=Path,required=True)
        if name=='audit':p.add_argument('--skip-asr',action='store_true',help='Not recommended: trust TTS without ASR verification')
        if name=='generate':p.add_argument('--real-speech',type=Path,help='Optional extra real-world speech clips')
        if name=='train':p.add_argument('--resume',action='store_true')
        if name=='all':
            p.add_argument('--skip-download',action='store_true');p.add_argument('--resume',action='store_true')
            p.add_argument('--auto-setup',action='store_true',help='Install Python dependencies automatically into current Python environment')
            p.add_argument('--real-speech',type=Path)
    args=parser.parse_args(argv)
    try:
        if args.action=='init':init_cmd(args);return
        if args.action=='setup':pip_setup(args);return
        paths,cfg=project_config(args.project);hw=detect()
        if args.action=='doctor':doctor(args,paths,cfg,hw)
        elif args.action=='record':
            from .voices import record_voice
            record_voice(paths,name=args.name)
        elif args.action=='export-voices':
            from .voices import export_voices
            export_voices(paths,args.file)
        elif args.action=='import-voices':
            from .voices import import_voices
            import_voices(paths,args.file)
        elif args.action=='download':
            from .download import download
            download(paths,cfg)
        elif args.action=='generate':
            from .tts import generate
            from .dataset import make_noise,import_speech
            from .golos import assert_ready
            assert_ready(paths,cfg)
            generate(args.project,paths,cfg,hw);make_noise(paths,cfg)
            if args.real_speech:import_speech(paths,args.real_speech)
        elif args.action=='audit':
            from .dataset import audit_and_assemble
            audit_and_assemble(paths,cfg,hw,skip_asr=args.skip_asr)
        elif args.action=='augment':aug_cmd(paths,cfg)
        elif args.action=='challenge':
            from .challenge import challenge
            challenge(paths,cfg)
        elif args.action=='features':fit_cmd('features',paths,cfg)
        elif args.action=='train':fit_cmd('train',paths,cfg,['--resume'] if args.resume else None)
        elif args.action=='evaluate':evaluate_cmd(paths,cfg)
        elif args.action=='export':export_cmd(paths,cfg)
        elif args.action=='status':status(paths,cfg)
        elif args.action=='all':
            if args.auto_setup:
                pip_setup(argparse.Namespace(confirm=True,break_system=False))
            from .download import download
            from .tts import generate
            from .dataset import audit_and_assemble,make_noise,import_speech
            if not args.skip_download:download(paths,cfg)
            from .golos import assert_ready
            assert_ready(paths,cfg)
            generate(args.project,paths,cfg,hw);make_noise(paths,cfg)
            if args.real_speech:import_speech(paths,args.real_speech)
            audit_and_assemble(paths,cfg,hw)
            aug_cmd(paths,cfg)
            from .challenge import challenge
            challenge(paths,cfg)
            fit_cmd('features',paths,cfg)
            fit_cmd('train',paths,cfg,['--resume'] if args.resume else None)
            evaluate_cmd(paths,cfg)
            export_cmd(paths,cfg)
    except (RuntimeError,ValueError,FileNotFoundError,ImportError,OSError) as exc:
        parser.exit(1,f'ERROR: {exc}\n')

if __name__=='__main__':main()
