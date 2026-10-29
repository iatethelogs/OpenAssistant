import sys, math, json
from pathlib import Path
import numpy as np
from PIL import Image, ImageSequence
from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QColor, QPainter, QImage
from PySide6.QtWidgets import (QApplication, QMainWindow, QWidget, QHBoxLayout, QVBoxLayout,
 QPushButton, QLabel, QSpinBox, QDoubleSpinBox, QCheckBox, QComboBox, QFileDialog,
 QMessageBox, QGroupBox, QFormLayout, QSlider)
from PySide6.QtSvg import QSvgRenderer

class Animation:
    def __init__(self, path):
        self.frames=[]; self.durations=[]
        if Path(path).suffix.lower()=='.svg':
            svg=QSvgRenderer(str(path))
            if not svg.isValid(): raise ValueError('Не удалось открыть SVG')
            fps=30; count=min(1800,max(1,round(svg.animationDuration()/1000*fps))) if svg.animated() else 1
            s=svg.defaultSize(); w=max(1,s.width()); h=max(1,s.height())
            k=min(1,800/max(w,h)); w=max(1,int(w*k)); h=max(1,int(h*k))
            for i in range(count):
                if svg.animated(): svg.setCurrentFrame(i)
                q=QImage(w,h,QImage.Format.Format_RGBA8888); q.fill(Qt.GlobalColor.transparent)
                p=QPainter(q); svg.render(p); p.end()
                raw=np.frombuffer(q.constBits(),dtype=np.uint8).reshape(h,q.bytesPerLine())[:,:w*4].reshape(h,w,4).copy()
                self.frames.append(Image.fromarray(raw,'RGBA')); self.durations.append(1000/fps)
        else:
            with Image.open(path) as src:
                for frame in ImageSequence.Iterator(src):
                    self.frames.append(frame.convert('RGBA').copy())
                    self.durations.append(max(20,frame.info.get('duration',src.info.get('duration',100)) or 100))
                    if len(self.frames)>=1800: break
        if not self.frames: raise ValueError('Нет кадров')
        self.ends=np.cumsum(self.durations); self.total=sum(self.durations)

    def frame(self,t,cross):
        if len(self.frames)==1:return self.frames[0]
        t=t%self.total; idx=min(len(self.frames)-1,int(np.searchsorted(self.ends,t,side='right')))
        if not cross:return self.frames[idx]
        alpha=(t-(self.ends[idx]-self.durations[idx]))/self.durations[idx]
        return Image.blend(self.frames[idx],self.frames[(idx+1)%len(self.frames)],alpha)

def to_led(im,w,h,fit,brightness,gamma):
    rgba=np.asarray(im.convert('RGBA'),dtype=np.float32)
    rgb=(rgba[:,:,:3]*(rgba[:,:,3:4]/255)).astype(np.uint8)
    gray=Image.fromarray(rgb,'RGB').convert('L')
    iw,ih=gray.size
    if fit=='Растянуть': gray=gray.resize((w,h),Image.Resampling.LANCZOS)
    else:
        k=min(w/iw,h/ih) if fit=='Вписать' else max(w/iw,h/ih)
        nw,nh=max(1,round(iw*k)),max(1,round(ih*k))
        gray=gray.resize((nw,nh),Image.Resampling.LANCZOS)
        if fit=='Вписать':
            dst=Image.new('L',(w,h)); dst.paste(gray,((w-nw)//2,(h-nh)//2));gray=dst
        else:
            x=(nw-w)//2;y=(nh-h)//2;gray=gray.crop((x,y,x+w,y+h))
    lum=np.asarray(gray,dtype=np.float32)/255
    a=np.uint8(np.round(255*np.power(np.clip(lum*brightness,0,1),gamma)))
    return np.repeat(a[:,:,None],3,axis=2)

class LedView(QWidget):
    def __init__(self):
        super().__init__();self.data=np.zeros((16,64,3),dtype=np.uint8);self.setMinimumSize(680,380)
    def show_frame(self,data):self.data=data;self.update()
    def paintEvent(self,event):
        p=QPainter(self);p.setRenderHint(QPainter.RenderHint.Antialiasing,True)
        p.fillRect(self.rect(),QColor(13,16,23))
        rows,cols=self.data.shape[:2]
        pitch=min((self.width()-20)/cols,(self.height()-20)/rows)
        radius=max(1,pitch*.38);sx=(self.width()-pitch*cols)/2;sy=(self.height()-pitch*rows)/2
        p.setPen(Qt.PenStyle.NoPen)
        for y in range(rows):
            for x in range(cols):
                cx=sx+(x+.5)*pitch;cy=sy+(y+.5)*pitch
                p.setBrush(QColor(32,36,44));p.drawEllipse(cx-radius,cy-radius,2*radius,2*radius)
                v=int(self.data[y,x,0])
                if v:
                    r=radius*.85;p.setBrush(QColor(v,v,v));p.drawEllipse(cx-r,cy-r,2*r,2*r)
        p.end()

class Window(QMainWindow):
    def __init__(self):
        super().__init__();self.anim=None;self.ms=0
        self.setWindowTitle('Мартин — симулятор монохромной матрицы');self.resize(1100,660)
        root=QWidget();self.setCentralWidget(root);layout=QHBoxLayout(root)
        panel=QWidget();panel.setFixedWidth(285);controls=QVBoxLayout(panel);layout.addWidget(panel)
        openbtn=QPushButton('Открыть GIF / APNG / WebP / SVG');openbtn.clicked.connect(self.open_animation);controls.addWidget(openbtn)
        self.fileinfo=QLabel('Файл не выбран');self.fileinfo.setWordWrap(True);controls.addWidget(self.fileinfo)
        box=QGroupBox('Матрица');form=QFormLayout(box);controls.addWidget(box)
        self.cols=QSpinBox();self.cols.setRange(1,256);self.cols.setValue(64);form.addRow('Ширина',self.cols)
        self.rows=QSpinBox();self.rows.setRange(1,128);self.rows.setValue(16);form.addRow('Высота',self.rows)
        self.fps=QSpinBox();self.fps.setRange(1,120);self.fps.setValue(30);form.addRow('FPS',self.fps)
        self.fit=QComboBox();self.fit.addItems(['Вписать','Заполнить','Растянуть']);form.addRow('Масштаб',self.fit)
        self.cross=QCheckBox('Промежуточные кадры');self.cross.setChecked(True);form.addRow(self.cross)
        self.gamma=QDoubleSpinBox();self.gamma.setRange(.2,3);self.gamma.setSingleStep(.1);self.gamma.setValue(1);form.addRow('Гамма',self.gamma)
        self.brightness=QSlider(Qt.Orientation.Horizontal);self.brightness.setRange(1,100);self.brightness.setValue(70);form.addRow('Яркость',self.brightness)
        self.seam=QCheckBox('Показать стык цилиндра');form.addRow(self.seam)
        self.pause=QPushButton('Пауза');self.pause.setCheckable(True);self.pause.clicked.connect(lambda v:self.pause.setText('Продолжить' if v else 'Пауза'));controls.addWidget(self.pause)
        raw=QPushButton('Экспорт RGB888 + JSON');raw.clicked.connect(self.export_raw);controls.addWidget(raw)
        gif=QPushButton('Экспорт GIF');gif.clicked.connect(self.export_gif);controls.addWidget(gif)
        self.info=QLabel();controls.addWidget(self.info);controls.addStretch()
        self.view=LedView();layout.addWidget(self.view,1)
        self.timer=QTimer(self);self.timer.timeout.connect(self.advance);self.timer.start(33)
        for control in [self.cols,self.rows,self.fps]:control.valueChanged.connect(self.refresh)
        self.fit.currentIndexChanged.connect(self.refresh)
        self.cross.toggled.connect(self.refresh)
        self.gamma.valueChanged.connect(self.refresh)
        self.brightness.valueChanged.connect(self.refresh)
        self.seam.toggled.connect(self.refresh)
        self.refresh()

    def open_animation(self):
        path,_=QFileDialog.getOpenFileName(self,'Открыть анимацию','','Анимации (*.gif *.png *.apng *.webp *.svg)')
        if not path:return
        try:self.anim=Animation(path);self.ms=0
        except Exception as e:QMessageBox.critical(self,'Ошибка',str(e));return
        self.fileinfo.setText(Path(path).name+f' ({len(self.anim.frames)} кадров)');self.refresh()

    def frame_at(self,t):
        w=self.cols.value();h=self.rows.value()
        if self.anim is None:return np.zeros((h,w,3),dtype=np.uint8)
        return to_led(self.anim.frame(t,self.cross.isChecked()),w,h,self.fit.currentText(),self.brightness.value()/100,self.gamma.value())

    def refresh(self,*args):
        frame=self.frame_at(self.ms)
        if self.seam.isChecked():frame=np.concatenate((frame,frame[:,:1]),axis=1)
        self.view.show_frame(frame)
        self.timer.setInterval(max(1,round(1000/self.fps.value())))
        self.info.setText(f'{self.cols.value()} × {self.rows.value()} = {self.cols.value()*self.rows.value()} LED')

    def advance(self):
        if not self.pause.isChecked():
            self.ms+=self.timer.interval();self.refresh()

    def frames_for_export(self):
        if self.anim is None:
            QMessageBox.warning(self,'Нет анимации','Откройте анимацию');return None
        step=1000/self.fps.value(); count=max(1,math.ceil(self.anim.total/step))
        if count>12000:QMessageBox.warning(self,'Слишком длинная анимация','Максимум 12000 кадров');return None
        return [self.frame_at(i*step) for i in range(count)]

    def export_raw(self):
        frames=self.frames_for_export()
        if frames is None:return
        name,_=QFileDialog.getSaveFileName(self,'Экспорт','martin_animation.rgb','RGB (*.rgb)')
        if not name:return
        path=Path(name).with_suffix('.rgb')
        with path.open('wb') as output:
            for frame in frames:output.write(frame.tobytes())
        meta=dict(width=self.cols.value(),height=self.rows.value(),frames=len(frames),fps=self.fps.value(),
                  format='RGB888',layout='row-major left-to-right top-to-bottom',frame_bytes=self.cols.value()*self.rows.value()*3,
                  loop=True,rgb_file=path.name)
        path.with_suffix('.json').write_text(json.dumps(meta,ensure_ascii=False,indent=2),encoding='utf-8')
        QMessageBox.information(self,'Экспорт',f'Сохранено {len(frames)} кадров')

    def export_gif(self):
        frames=self.frames_for_export()
        if frames is None:return
        name,_=QFileDialog.getSaveFileName(self,'Экспорт GIF','martin_preview.gif','GIF (*.gif)')
        if not name:return
        scale=max(2,min(14,640//self.cols.value()))
        out=[Image.fromarray(f,'RGB').resize((self.cols.value()*scale,self.rows.value()*scale),Image.Resampling.NEAREST) for f in frames]
        out[0].save(str(Path(name).with_suffix('.gif')),save_all=True,append_images=out[1:],duration=round(1000/self.fps.value()),loop=0,optimize=False)
        QMessageBox.information(self,'Экспорт','GIF сохранён')

if __name__=='__main__':
    app=QApplication(sys.argv);window=Window();window.show();sys.exit(app.exec())
