"""Offline checks for orchestration and media manifests without downloading models."""
import json
import tempfile
import unittest
from pathlib import Path
from argparse import Namespace

from wakeforge.core import default_config, detect, norm, project_paths,save_json
from wakeforge.tts import jobs_for, render_tts,source_path

class Smoke(unittest.TestCase):
    def test_generic_keyword(self):
        cfg=default_config('Алиса',voices=20,variants=2,hardware={'tts_gpus':[],'train_gpu':-1,'audio_workers':2})
        self.assertNotIn('Мартин',cfg['keyword'])
        self.assertTrue(any(x.startswith('ал') for x in cfg['negative_words']))
        tasks=jobs_for(cfg)
        self.assertEqual(len(tasks),20*(2+cfg['negative_per_voice']+cfg['generic_speech_per_voice']))
        self.assertTrue(all(r['kind']=='positive' and r['text']=='Алиса' for r in tasks if r['kind']=='positive'))
        splits={r['split'] for r in tasks if r['kind']=='positive'}
        self.assertEqual(splits,{'train','val','test'})
        split_per_speaker={}
        for task in tasks:
            split_per_speaker.setdefault(task['speaker'],set()).add(task['split'])
        self.assertTrue(all(len(x)==1 for x in split_per_speaker.values()))

    def test_waveform(self):
        import numpy as np
        r=dict(id='a1',kind='positive')
        w=np.sin(np.linspace(0,5000,16000,dtype=np.float32))*.2
        clip,a,b=render_tts(w,16000,r)
        self.assertEqual(clip.shape,(64000,))
        self.assertTrue(0<a<b<64000)
        self.assertAlmostEqual(b/16000,3.35,delta=.1)

    def test_split_paths(self):
        with tempfile.TemporaryDirectory() as d:
            p=project_paths(d);rec={'kind':'positive','split':'test','id':'v2'}
            self.assertEqual(source_path(p,rec),Path(d)/'positives/data/test/clean/v2.flac')
            self.assertEqual(norm('МАРТИН!!'), 'мартин')

if __name__=='__main__':unittest.main()
