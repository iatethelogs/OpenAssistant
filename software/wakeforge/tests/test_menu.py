import unittest
from unittest.mock import patch
from wakeforge import menu


class MenuSmokeTests(unittest.TestCase):
    def test_menu_opens_and_exits_without_project(self):
        with patch('wakeforge.menu.detect',return_value={'cpu_threads':2,'gpus':[],
                  'tts_gpus':[],'train_gpu':-1,'audio_workers':1}), \
             patch('wakeforge.menu.sys.stdin.isatty',return_value=True), \
             patch('builtins.input',return_value='0'):
            self.assertEqual(menu.main_menu(),0)

    def test_enter_selects_new_wizard(self):
        answers=iter(['','0'])
        with patch('wakeforge.menu.detect',return_value={'cpu_threads':2,'gpus':[],
                  'tts_gpus':[],'train_gpu':-1,'audio_workers':1}), \
             patch('wakeforge.menu.sys.stdin.isatty',return_value=True), \
             patch('builtins.input',side_effect=lambda _:next(answers)), \
             patch('wakeforge.menu.create_wizard',return_value=None) as make:
            self.assertEqual(menu.main_menu(),0)
            make.assert_called_once()

if __name__=='__main__':unittest.main()
