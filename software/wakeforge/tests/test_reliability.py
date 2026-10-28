import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from wakeforge.core import default_config,validate,project_paths,save_json
from wakeforge.menu import configure_counts, recommended_counts
from wakeforge.resources import preset_groups, dataset_count, storage_estimate_gib, hardware_tier, suggest_workers, recommend_batch
from wakeforge.tts import valid_labeled
from wakeforge.guide import outputs_exist

class CountTests(unittest.TestCase):
    def cfg(self):
        return default_config('Луна', hard=['лук'],voices=48,
                              hardware={'tts_gpus': [],'train_gpu':-1,'audio_workers':2})

    def test_count_recommendation_math(self):
        hw=dict(cpu_threads=8,gpus=[dict(index=0,mb=10240)],tts_gpus=[0],ram_mb=16384)
        c={**self.cfg(),**preset_groups(hw)['2'][1]}
        n=recommended_counts(c)
        self.assertEqual(n['positive'],20000)
        self.assertEqual(n['hard'],15000)
        self.assertEqual(n['speech'],5000)
        self.assertEqual(n['augclean'],120000)
        self.assertEqual(n['hardmix'],80000)
        self.assertEqual(n['challenge'],1000)
        self.assertEqual(n['noise'],5000)
        self.assertEqual(n['tts'],40000)
        self.assertEqual(n['total'],246000)

    def test_prompt_all_core_counts_and_custom_edit(self):
        c=self.cfg()
        # custom preset 1, then change synthetic voices 48->72, all others Enter,
        # no additional settings
        answers=['1','72']+['']*7+['н']
        hw=dict(cpu_threads=8,gpus=[dict(index=0,mb=10240)],tts_gpus=[0],ram_mb=16384)
        with patch('builtins.input',side_effect=answers) as ask:
            configure_counts(c,hw)
        self.assertEqual(c['voice_profiles'],72)
        self.assertEqual(c['variants_per_voice'],3)
        self.assertEqual(recommended_counts(c)['positive'],216)
        validate(c)
        self.assertEqual(ask.call_count,10)

    def test_hard_negative_cannot_contain_keyword(self):
        c=self.cfg()
        c['negative_words']=['Эй Луна']
        with self.assertRaisesRegex(ValueError,'точное слово'):validate(c)
        c['negative_words']=['лук']
        c['generic_phrases']=['Скажи Луна пожалуйста']
        with self.assertRaisesRegex(ValueError,'слово активации'):validate(c)

    def test_no_zero_augment_when_training_requires_it(self):
        c=self.cfg();c['augclean_multiplier']=0
        with self.assertRaisesRegex(ValueError,'должен быть'):validate(c)

    def test_incomplete_download_marker_is_not_completion(self):
        with tempfile.TemporaryDirectory() as path:
            root=project_paths(path);c=self.cfg();save_json(root['config'],c)
            self.assertFalse(outputs_exist(root,'download'))
            save_json(root['cache']/'downloads.json',dict(tts_model=c['tts_model'],tts_cache=path,acoustics=True))
            self.assertFalse(outputs_exist(root,'download'))

    def test_negative_noise_has_both_quiet_and_loud_samples(self):
        import numpy as np
        import soundfile as sf
        from wakeforge.dataset import make_noise
        with tempfile.TemporaryDirectory() as path:
            root=project_paths(path);c=self.cfg()
            c['voice_profiles']=12
            make_noise(root,c)
            files=sorted((root['negative']/'train'/'negative'/'noise').glob('*.flac'))
            self.assertGreaterEqual(len(files),8)
            peaks=[np.max(np.abs(sf.read(f,dtype='float32')[0])) for f in files[:8]]
            self.assertLess(peaks[0],.15)
            self.assertGreater(peaks[4],.6)
            self.assertGreater(peaks[6],.8)
            self.assertTrue((root['negative']/'noise_plan.json').is_file())

    def test_missing_feature_completion_sentinel(self):
        with tempfile.TemporaryDirectory() as path:
            root=project_paths(path);c=self.cfg();save_json(root['config'],c)
            save_json(root['training']/'feature_plan.json',{'partial':True})
            self.assertFalse(outputs_exist(root,'features'))
            save_json(root['training']/'training_plan.json',{'partial':True})
            self.assertFalse(outputs_exist(root,'train'))

    def test_microphone_device_selection(self):
        from types import SimpleNamespace
        from wakeforge.voices import _choose_microphone
        dev=[{'name':'Speakers','max_input_channels':0,'hostapi':0,'default_samplerate':48000},
             {'name':'USB Microphone','max_input_channels':1,'hostapi':0,'default_samplerate':48000},
             {'name':'Built-in mic','max_input_channels':2,'hostapi':0,'default_samplerate':44100}]
        sd=SimpleNamespace(query_devices=lambda:dev,query_hostapis=lambda:[{'name':'WASAPI'}],
                           default=SimpleNamespace(device=(2,0)))
        with patch('builtins.input',side_effect=['1']):
            self.assertEqual(_choose_microphone(sd),1)
        with patch('builtins.input',side_effect=['']):
            self.assertEqual(_choose_microphone(sd),2)

    def test_incomplete_generated_audio_is_not_completion(self):
        with tempfile.TemporaryDirectory() as path:
            root=project_paths(path);c=self.cfg();save_json(root['config'],c)
            save_json(root['root']/'tts_plan.json',{})
            self.assertFalse(outputs_exist(root,'generate'))

if __name__=='__main__':unittest.main()

class SingleGpuPresetTests(unittest.TestCase):
    def hw(self, mb, count=1):
        return dict(cpu_threads=12,ram_mb=16384,
            gpus=[dict(index=i,mb=mb) for i in range(count)],
            tts_gpus=list(range(count)),audio_workers=6,train_gpu=0)

    def test_one_gpu_not_sum_vram(self):
        one=preset_groups(self.hw(10240,1))['2'][1]
        five=preset_groups(self.hw(10240,5))['2'][1]
        self.assertEqual(one,five)
        self.assertEqual(hardware_tier(self.hw(10240,5)),'8-11')
        self.assertNotIn('batch',one)
        self.assertEqual(recommend_batch(self.hw(10240,1),0),32)

    def test_12gb_and_24gb_recommendations(self):
        a=preset_groups(self.hw(12288))['2'][1]
        b=preset_groups(self.hw(24576))['2'][1]
        self.assertEqual(a['voice_profiles'],5000)
        self.assertEqual(b['voice_profiles'],5000)
        self.assertEqual(dataset_count(b)['total'],dataset_count(a)['total'])
        self.assertEqual(recommend_batch(self.hw(12288),0),64)
        self.assertEqual(recommend_batch(self.hw(24576),0),128)
        self.assertGreater(storage_estimate_gib(dataset_count(b))[0],10)

    def test_cpu_profile_keeps_dataset_size(self):
        cpu = dict(gpus=[],tts_gpus=[],cpu_threads=8,train_gpu=-1)
        cfg=preset_groups(cpu)['2'][1]
        self.assertEqual(dataset_count(cfg)['total'],246000)
        self.assertEqual(recommend_batch(cpu),16)

    def test_ram_limits_workers(self):
        self.assertEqual(suggest_workers(dict(cpu_threads=32,ram_mb=4096)),1)
        self.assertEqual(suggest_workers(dict(cpu_threads=32,ram_mb=16384)),5)
        self.assertEqual(suggest_workers(dict(cpu_threads=32,ram_mb=65536)),8)

    def test_hot_microphone_preserves_loud_positive(self):
        import numpy as np
        from wakeforge.legacy.augment import loud_microphone
        src=np.sin(np.linspace(0,60*np.pi,64000)).astype(np.float32)*.15
        class Force:
            def random(self):return 0.
            def uniform(self,a,b):return 2.0
        out, label=loud_microphone(src,Force(),'hardmix')
        self.assertEqual(out.shape,src.shape)
        self.assertEqual(label['type'],'hot_microphone')
        self.assertAlmostEqual(float(np.max(np.abs(out))),.96,places=2)
        self.assertTrue(np.all(np.isfinite(out)))



class HardwareIndependentDatasetTests(unittest.TestCase):
    def test_identical_preset_count_across_all_devices(self):
        machines = [
            dict(cpu_threads=4,ram_mb=4096,gpus=[],tts_gpus=[],train_gpu=-1),
            dict(cpu_threads=8,ram_mb=8192,gpus=[dict(index=0,mb=8192)],tts_gpus=[0],train_gpu=0),
            dict(cpu_threads=12,ram_mb=16384,gpus=[dict(index=0,mb=12288)],tts_gpus=[0],train_gpu=0),
            dict(cpu_threads=32,ram_mb=65536,gpus=[dict(index=i,mb=10240) for i in range(5)],tts_gpus=list(range(5)),train_gpu=0),
        ]
        for mode in ('1','2','3'):
            all_counts=[dataset_count(preset_groups(m)[mode][1]) for m in machines]
            self.assertTrue(all(n==all_counts[0] for n in all_counts),all_counts)

    def test_train_batch_does_not_change_record_count(self):
        machine=dict(cpu_threads=12,ram_mb=16384,gpus=[dict(index=0,mb=8192),dict(index=1,mb=24576)],tts_gpus=[0,1],train_gpu=0)
        config=dict(preset_groups(machine)['2'][1])
        x=dataset_count(config)
        config['batch']=recommend_batch(machine,0)
        self.assertEqual(dataset_count(config),x)
        config['batch']=recommend_batch(machine,1)
        self.assertEqual(dataset_count(config),x)
