"""Offline tests: official Golos corpus inclusion is mandatory."""
import io
import json
import tarfile
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np
from wakeforge.core import default_config, project_paths
from wakeforge.golos import (assert_ready, prepare_golos, target_count,
                             transcription, training_member)


def tar_fixture(path):
    with tarfile.open(path,'w:gz') as archive:
        for i in range(75):
            key = f'train/crowd/{i:05d}'
            phrase = 'Мартин, ответь' if i%10==0 else 'Пожалуйста включи свет в комнате'
            for ext,body in (('.json',json.dumps({'text':phrase}).encode()),
                             ('.opus',bytes([i%255])*2048)):
                info=tarfile.TarInfo(key+ext)
                info.size=len(body)
                archive.addfile(info,io.BytesIO(body))
        # Test partition must never be used for training negatives
        for ext,body in (('.json',b'{"text":"test phrase"}'),('.opus',b'abc')):
            info=tarfile.TarInfo('test/crowd/00002'+ext)
            info.size=len(body)
            archive.addfile(info,io.BytesIO(body))


class GolosTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.paths=project_paths(Path(self.tmp.name)/'sample')
        self.cfg=default_config('Мартин',hard=['Маша'],voices=24, variants=1,
                                hardware={'tts_gpus':[],'train_gpu':-1,'audio_workers':1})
        self.cfg['workers']=1

    def test_transcripts_and_training_filter(self):
        self.assertEqual(transcription('{"text":"Молоко"}'.encode(), 'x.json'),'Молоко')
        self.assertEqual(transcription('{"metadata":{"transcription":"Привет"}}'.encode(),'x.json'),'Привет')
        self.assertFalse(training_member('test/crowd/001.opus'))
        self.assertTrue(training_member('train/crowd/001.opus'))

    def test_golos_cannot_be_missing(self):
        with self.assertRaisesRegex(RuntimeError,'Golos'):
            assert_ready(self.paths,self.cfg)
        self.assertEqual(target_count(self.cfg),100)

    def test_only_transcribed_nonkeyword_samples_are_selected(self):
        archive=Path(self.tmp.name)/'small.tar.gz'
        tar_fixture(archive)
        fake=np.full(64000,.02,dtype=np.float32)
        with patch('wakeforge.golos.decode_audio',return_value=fake):
            result=prepare_golos(self.paths,self.cfg,archive,required=3)
        self.assertEqual(result['created'],3)
        self.assertEqual(len(list((self.paths['negative']/'train'/'negative'/'speech').glob('golos_*.flac'))),3)
        self.assertFalse((self.paths['negative']/'test').exists())
        with self.assertRaisesRegex(RuntimeError,'Golos'):
            assert_ready(self.paths,self.cfg) # small fixture not enough for real plan


if __name__=='__main__':unittest.main()
