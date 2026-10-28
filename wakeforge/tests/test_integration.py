import json, tempfile, unittest
from pathlib import Path
import numpy as np
import soundfile as sf
from wakeforge.core import project_paths,default_config,save_json
from wakeforge.tts import jobs_for,source_path
from wakeforge.dataset import audit_and_assemble,make_noise
from wakeforge.legacy.augment import split_bucket

class Integration(unittest.TestCase):
 def test_manifests_augmentation_and_fit_plan(self):
  with tempfile.TemporaryDirectory() as temp:
   p=project_paths(temp)
   c=default_config('Луна',hard=['лужа','лунава','лужица','лук'],voices=16,variants=2,hardware={'tts_gpus':[],'train_gpu':-1,'audio_workers':2})
   c['voice_profiles']=16;c['workers']=2
   save_json(p['config'],c)
   for r in jobs_for(c):
    dest=source_path(p,r);dest.parent.mkdir(parents=True,exist_ok=True)
    x=np.zeros(64000,dtype=np.float32)
    t=np.arange(6400,dtype=np.float32)/16000
    x[42000:48400]=.17*np.sin(2*np.pi*330*t)
    sf.write(dest,x,16000,format='FLAC')
    save_json(dest.with_suffix('.json'),dict(file=dest.relative_to(p['positive'] if r['kind']=='positive' else p['negative']).as_posix(),
      sample_rate=16000,label=int(r['kind']=='positive'),keyword=c['keyword'],word_start_sample=42000,
      word_end_sample=48400,profile=r['speaker']))
   make_noise(p,c)
   audit_and_assemble(p,c,{'tts_gpus':[],'cpu_threads':4},skip_asr=True)
   # Manufacture several short impulse responses in every split.
   for i in range(80):
    q=p['acoustics']/'slr26'/f'room{i:03d}.wav'
    q.parent.mkdir(parents=True,exist_ok=True)
    rir=np.zeros(4000,dtype=np.float32);rir[0]=1.0;rir[900]=.25
    sf.write(q,rir,16000)
   categories={split_bucket(str(x),'rir') for x in (p['acoustics']/'slr26').glob('*.wav')}
   self.assertEqual(categories,{'train','val','test'})
   import subprocess,sys,os
   env=dict(os.environ,PYTHONPATH=str(Path(__file__).resolve().parent.parent))
   args=[sys.executable,'-m','wakeforge.legacy.augment','--word','Луна',
     '--positive',str(p['positive']),'--negative',str(p['negative']),
     '--acoustics',str(p['acoustics']),'--out',str(p['augmented']),
     '--workers','2','--shard-size','2',
     '--augclean-train','2','--hardmix-train','2','--augclean-val','2','--hardmix-val','2',
     '--augclean-test','2','--hardmix-test','2']
   ret=subprocess.run(args,env=env,capture_output=True,text=True,timeout=45)
   self.assertEqual(ret.returncode,0,ret.stdout[-1600:]+ret.stderr[-1600:])
   self.assertEqual(len((p['augmented']/'positive_augclean_train.jsonl').read_text().splitlines()),2)
   from wakeforge.challenge import challenge
   c['challenge_count']=2
   challenge(p,c)
   challenge_records=[json.loads(x) for x in (p['positive']/'challenge_val.jsonl').read_text().splitlines()]
   self.assertEqual(len(challenge_records),2)
   self.assertTrue(all(Path(x['file']).is_file() for x in challenge_records))
   self.assertNotIn('1000000',str(p['augmented']/'positive_hardmix_val.jsonl'))
   # Explicitly check that generalized target is accepted by features planner.
   from wakeforge.legacy.fit import plan
   args=type('A',(),dict(positive=p['positive'],augmented=p['augmented'],negative=p['negative'],
       keyword='Луна',shard_size=16,negative_eval=10,ambient_val=10,ambient_test=10))
   plan_out,counts=plan(args)
   self.assertEqual(counts['train/positive'],24)
   self.assertEqual(counts['train/hardmix'],2)

if __name__=='__main__':unittest.main()
