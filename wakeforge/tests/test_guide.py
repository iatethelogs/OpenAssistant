import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from wakeforge.core import project_paths, default_config, save_json
from wakeforge.guide import STEPS, get_state, mark_done, outputs_exist, guided_pipeline


def fake_artifacts(paths, stage):
    def touch(path):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b'example')
    if stage == 'download':
        paths['cache'].mkdir(parents=True,exist_ok=True)
        folder=paths['cache']/'fake-tts';folder.mkdir(exist_ok=True)
        from wakeforge.core import project_config,save_json
        cfg=project_config(paths['root'])[1]
        save_json(paths['cache']/'downloads.json',dict(tts_model=cfg['tts_model'],
            tts_cache=str(folder),acoustics=True))
        for label in ('slr26','slr28'):
            touch(paths['acoustics']/label/'.wakeforge_extracted')
        from wakeforge.golos import target_count
        n=target_count(cfg)
        save_json(paths['cache']/'golos'/'prepared.json',
                  {'keyword':cfg['keyword'].lower(),'required':n,'created':n,
                   'seed':cfg['seed'],'files':[f'golos_{i}.flac' for i in range(n)]})
        for i in range(n):
            file = paths['negative'] / 'train' / 'negative' / 'speech' / f'golos_{i}.flac'
            file.parent.mkdir(parents=True,exist_ok=True)
            file.write_bytes(b'0' * 101)
    elif stage == 'generate':
        from wakeforge.core import project_config
        from wakeforge.tts import jobs_for, source_path
        _, cfg=project_config(paths['root'])
        touch(paths['root']/'tts_plan.json')
        for job in jobs_for(cfg):
            touch(source_path(paths,job))
    elif stage == 'audit':
        from wakeforge.core import save_json
        save_json(paths['positive']/'quality_report.json',{'accepted':36,'missing':0,'rejected':1})
        for split in ('train','val','test'):
            touch(paths['positive']/f'positive_{split}.jsonl')
    elif stage == 'augment':
        from wakeforge.core import project_config
        c=project_config(paths['root'])[1]
        for split in ('train','val','test'):
            for kind,field in (('augclean','augclean_multiplier'),('hardmix','hardmix_multiplier')):
                file=paths['augmented']/f'positive_{kind}_{split}.jsonl'
                file.parent.mkdir(parents=True,exist_ok=True)
                file.write_bytes(b'example\n'*c[field])
    elif stage == 'challenge':
        from wakeforge.core import project_config
        count=project_config(paths['root'])[1]['challenge_count']
        for split in ('val','test'):
            file=paths['positive']/f'challenge_{split}.jsonl'
            file.parent.mkdir(parents=True,exist_ok=True)
            file.write_bytes(b'example\n'*count)
    elif stage == 'features':
        import hashlib
        file=paths['training']/'feature_plan.json'
        touch(file)
        (paths['training']/'.wakeforge_features.done').write_text(hashlib.sha256(file.read_bytes()).hexdigest())
    elif stage == 'train':
        import hashlib
        plan=paths['training']/'training_plan.json';touch(plan)
        (paths['training']/'.wakeforge_training.done').write_text(hashlib.sha256(plan.read_bytes()).hexdigest())
        touch(paths['training']/'runs/gpu0_wakeword/model/tflite_stream_state_internal_quant/stream_state_internal_quant.tflite')
    elif stage == 'evaluate':
        touch(paths['training']/'evaluation/report_val.json')
        touch(paths['training']/'evaluation/report_test.json')
    elif stage == 'export':
        touch(paths['models']/'example.tflite')


class GuideTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.paths = project_paths(Path(self.temp.name)/'projects'/'luna')
        self.cfg = default_config('Луна', hard=['лужа','лук'],voices=24,variants=2,
                                  hardware={'tts_gpus': [],'train_gpu':-1,'audio_workers':2})
        self.cfg['workers']=2
        save_json(self.paths['config'],self.cfg)

    def test_full_guide_all_stages_in_order(self):
        seen=[]
        def run(stage):
            seen.append(stage)
            fake_artifacts(self.paths, stage)
            return True
        with patch('wakeforge.guide._preflight',return_value=True), \
             patch('wakeforge.tts.valid_labeled',side_effect=lambda path,job:path.exists()), \
             patch('wakeforge.menu.show_plan'):
            ok=guided_pipeline(self.paths['root'],confirm=lambda p,d=True:True,
                               ask=lambda p,d=None:'2',run_stage=run)
        self.assertTrue(ok)
        self.assertEqual(seen,[x[0] for x in STEPS])
        state=get_state(self.paths,self.cfg)
        self.assertEqual(len(state['completed']),len(STEPS))

    def test_failure_and_resume_skips_completed(self):
        seen=[]
        def fail_on_audit(stage):
            seen.append(stage)
            if stage=='audit': return False
            fake_artifacts(self.paths, stage)
            return True
        with patch('wakeforge.guide._preflight',return_value=True), \
             patch('wakeforge.tts.valid_labeled',side_effect=lambda path,job:path.exists()), \
             patch('wakeforge.menu.show_plan'):
            result=guided_pipeline(self.paths['root'],confirm=lambda p,d=True:True,
                                   ask=lambda p,d=None:'2' if 'После каждого' in p else 'п',
                                   run_stage=fail_on_audit)
        self.assertFalse(result)
        self.assertEqual(seen,['download','generate','audit'])
        seen=[]
        def succeed(stage):
            seen.append(stage);fake_artifacts(self.paths,stage);return True
        with patch('wakeforge.guide._preflight',return_value=True), \
             patch('wakeforge.tts.valid_labeled',side_effect=lambda path,job:path.exists()), \
             patch('wakeforge.menu.show_plan'):
            result=guided_pipeline(self.paths['root'],confirm=lambda p,d=True:True,
                                   ask=lambda p,d=None:'2',run_stage=succeed)
        self.assertTrue(result)
        self.assertEqual(seen,[x[0] for x in STEPS][2:])

    def test_changed_settings_clears_progress(self):
        state=get_state(self.paths,self.cfg)
        fake_artifacts(self.paths,'download')
        mark_done(self.paths,state,'download')
        edited=dict(self.cfg, voice_profiles=40)
        save_json(self.paths['config'],edited)
        self.assertEqual(get_state(self.paths,edited)['completed'],[])

    def test_incomplete_artifacts_not_marked_success(self):
        seen=[]
        with patch('wakeforge.guide._preflight',return_value=True), \
             patch('wakeforge.tts.valid_labeled',side_effect=lambda path,job:path.exists()), \
             patch('wakeforge.menu.show_plan'):
            result=guided_pipeline(self.paths['root'],confirm=lambda p,d=True:True,
                                   ask=lambda p,d=None:'2' if 'После каждого' in p else 'п',
                                   run_stage=lambda s:seen.append(s) or True)
        self.assertFalse(result)
        self.assertEqual(get_state(self.paths,self.cfg)['completed'],[])

if __name__=='__main__':unittest.main()
