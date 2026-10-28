import json
import math
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from types import ModuleType

import numpy as np
import soundfile as sf

from wakeforge.core import project_paths, default_config, save_json
from wakeforge.voices import (record_voice, list_voices, export_voices,
                              import_voices, _audio_check, PROMPTS)
from wakeforge.tts import real_jobs_for,worker,source_path,jobs_for
from wakeforge.dataset import audit_and_assemble


def voice_signal(seconds=7):
    rate=24000
    t=np.arange(int(rate*seconds))/rate
    return (.08*np.sin(2*np.pi*233*t) + .03*np.sin(2*np.pi*517*t)).astype('float32')


def make_voice(paths,who):
    folder=paths['root']/'voices'/who
    folder.mkdir(parents=True,exist_ok=True)
    signal=voice_signal(3)
    sf.write(folder/'reference.wav',signal,24000)
    import hashlib
    digest=hashlib.sha256((folder/'reference.wav').read_bytes()).hexdigest()
    save_json(folder/'voice.json',dict(id=who,name=who,ref='reference.wav',
        ref_text=PROMPTS[0],seconds=3.0,sha256=digest,consent_confirmed=True,takes=[]))

class RecordingTests(unittest.TestCase):
    def test_record_voice_and_pack_roundtrip(self):
        with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b:
            paths=project_paths(a)
            sd=ModuleType('sounddevice')
            sd.play=lambda *v:None
            sd.wait=lambda:None
            answers=['да','Маша']
            for i in range(3):answers+=['','', '1']  # start, confirm complete transcript, save
            with patch.dict(sys.modules,{'sounddevice':sd}), \
                 patch('wakeforge.voices._choose_microphone',return_value=0), \
                 patch('wakeforge.voices._microphone_check',return_value=True), \
                 patch('wakeforge.voices.capture',return_value=voice_signal()), \
                 patch('wakeforge.voices.time.sleep'), \
                 patch('builtins.input',side_effect=answers):
                item=record_voice(paths,prompts=list(PROMPTS[:3]))
            self.assertEqual(item['id'],'masha')
            self.assertEqual(len(item['takes']),3)
            self.assertTrue((paths['root']/'voices'/'masha'/'reference.wav').is_file())
            package=Path(a)/'voice-pack.zip'
            export_voices(paths,package)
            self.assertEqual(import_voices(project_paths(b),package),1)
            self.assertEqual(len(list_voices(project_paths(b))),1)
            with self.assertRaises(RuntimeError):
                import_voices(project_paths(b),package)

    def test_clip_rejected(self):
        self.assertFalse(_audio_check(np.ones(24000,dtype='float32'),24000)[0])
        self.assertFalse(_audio_check(np.zeros(24000,dtype='float32'),24000)[0])
        self.assertTrue(_audio_check(voice_signal(),24000)[0])

    def test_speaker_split_and_jobs(self):
        with tempfile.TemporaryDirectory() as root:
            p=project_paths(root);cfg=default_config('Луна',hard=['лужа'],voices=12,variants=1)
            for n in range(2):make_voice(p,f'speaker{n}')
            rows=real_jobs_for(cfg,p)
            self.assertEqual(len(rows),2*(24+12+12))
            self.assertEqual({r['split'] for r in rows},{'train'})
            self.assertEqual(len({r['id'] for r in rows}),len(rows))
            for r in rows:
                self.assertTrue(str(source_path(p,r)).startswith(root))
            for n in range(10):make_voice(p,f'ten{n}')
            rows=real_jobs_for(cfg,p)
            split_per_voice={}
            for row in rows:
                split_per_voice.setdefault(row['voice_id'],set()).add(row['split'])
            self.assertTrue(all(len(x)==1 for x in split_per_voice.values()))
            self.assertEqual({next(iter(v)) for v in split_per_voice.values()}, {'train','val','test'})

    def test_clone_base_model_and_audit(self):
        class MockQwen:
            prompt_calls=0
            design_calls=0
            clone_calls=0
            @classmethod
            def from_pretrained(cls,name,**options):
                assert name.endswith('Base')
                return cls()
            def create_voice_clone_prompt(self,**kw):
                self.__class__.prompt_calls+=1
                assert kw['x_vector_only_mode'] is False
                return ['prompt']
            def generate_voice_clone(self,**kw):
                self.__class__.clone_calls+=1
                assert kw['voice_clone_prompt']==['prompt']
                y=np.sin(2*np.pi*350*np.arange(16000)/16000).astype('float32')*.12
                return [y],16000
        with tempfile.TemporaryDirectory() as root:
            p=project_paths(root)
            cfg=default_config('Луна',hard=['лужа'],voices=12,variants=1)
            cfg.update(real_positive_per_voice=2,real_hard_per_voice=1,real_speech_per_voice=1,
                       voice_gpus=[],workers=2)
            save_json(p['config'],cfg)
            make_voice(p,'alex')
            qwen=ModuleType('qwen_tts')
            qwen.Qwen3TTSModel=MockQwen
            with patch.dict(sys.modules,{'qwen_tts':qwen}):
                self.assertEqual(worker(root,'cpu',0,1,mode='clone'),0)
            self.assertEqual(MockQwen.prompt_calls,1)
            self.assertEqual(MockQwen.clone_calls,4)
            real=real_jobs_for(cfg,p)
            self.assertTrue(all(source_path(p,r).is_file() for r in real))
            # Synthetic (unseen) speakers are generated via a stubbed clean base.
            for r in jobs_for(cfg):
                if r['kind']!='positive':continue
                path=source_path(p,r);path.parent.mkdir(parents=True,exist_ok=True)
                x=np.zeros(64000,dtype='float32');x[45000:55000]=.12
                sf.write(path,x,16000)
                save_json(path.with_suffix('.json'),dict(
                    file=path.relative_to(p['positive']).as_posix(),sample_rate=16000,
                    label=1,word_start_sample=45000,word_end_sample=55000))
            audit_and_assemble(p,cfg,{'tts_gpus':[],'cpu_threads':2},skip_asr=True)
            rows=[json.loads(line) for line in (p['positive']/'positive_train.jsonl').read_text().splitlines()]
            self.assertEqual(sum(bool(row.get('real_voice')) for row in rows),2)
            self.assertEqual(sum('alex' in str(row) for row in rows),2)

if __name__=='__main__':unittest.main()
