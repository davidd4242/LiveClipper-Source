import {
  useEffect,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type RefObject,
} from 'react';
import './AudioMixer.css';

interface AudioMixerProps {
  voiceVolume: number;
  musicVolume: number;
  effectVolume: number;
  masterVolume: number;
  videoRef?: RefObject<HTMLVideoElement | null>;
  isPlaying?: boolean;
  onChange: (channel: 'voice' | 'music' | 'effect' | 'master', volume: number) => void;
}

type Channel = {
  id: 'voice' | 'music' | 'effect';
  label: string;
  volume: number;
};

type MeterValues = {
  left: number;
  right: number;
  peakLeft: number;
  peakRight: number;
  momentary: number;
  shortTerm: number;
  integrated: number;
  truePeak: number;
};

type AudioGraph = {
  context: AudioContext;
  master: GainNode;
  splitter: ChannelSplitterNode;
  analyserLeft: AnalyserNode;
  analyserRight: AnalyserNode;
  connected: WeakSet<HTMLMediaElement>;
};

const DB_FLOOR = -60;
const graphCache = new WeakMap<HTMLVideoElement, AudioGraph>();

const emptyMeter: MeterValues = {
  left: DB_FLOOR,
  right: DB_FLOOR,
  peakLeft: DB_FLOOR,
  peakRight: DB_FLOOR,
  momentary: DB_FLOOR,
  shortTerm: DB_FLOOR,
  integrated: DB_FLOOR,
  truePeak: DB_FLOOR,
};

/** 推子读数的数字部分（保留负号）；单位 dB 在页脚统一标一次，窄条里不再逐条重复。 */
function dbValue(volume: number): string {
  if (volume <= 0) return '−∞';
  return (20 * Math.log10(volume)).toFixed(1);
}

function dbLabel(volume: number): string {
  return `${dbValue(volume)} dB`;
}

function clampDb(value: number): number {
  if (!Number.isFinite(value)) return DB_FLOOR;
  return Math.min(0, Math.max(DB_FLOOR, value));
}

function formatDb(value: number): string {
  return value <= DB_FLOOR ? '−∞' : value.toFixed(1);
}

function connectMediaElement(graph: AudioGraph, element: HTMLMediaElement) {
  if (graph.connected.has(element)) return;
  try {
    const source = graph.context.createMediaElementSource(element);
    source.connect(graph.master);
    graph.connected.add(element);
  } catch {
    // The element may already belong to another audio graph; leave it alone.
  }
}

function ensureGraph(video: HTMLVideoElement, audioElements: HTMLAudioElement[] = []): AudioGraph | null {
  const cached = graphCache.get(video);
  if (cached) {
    audioElements.forEach((element) => connectMediaElement(cached, element));
    return cached;
  }
  try {
    const AudioContextClass =
      window.AudioContext ??
      (window as Window & { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioContextClass) return null;
    const context = new AudioContextClass();
    const master = context.createGain();
    const splitter = context.createChannelSplitter(2);
    const analyserLeft = context.createAnalyser();
    const analyserRight = context.createAnalyser();
    analyserLeft.fftSize = 1024;
    analyserRight.fftSize = 1024;
    analyserLeft.smoothingTimeConstant = 0.55;
    analyserRight.smoothingTimeConstant = 0.55;
    master.connect(context.destination);
    master.connect(splitter);
    splitter.connect(analyserLeft, 0);
    splitter.connect(analyserRight, 1);
    const graph: AudioGraph = {
      context,
      master,
      splitter,
      analyserLeft,
      analyserRight,
      connected: new WeakSet<HTMLMediaElement>(),
    };
    connectMediaElement(graph, video);
    audioElements.forEach((element) => connectMediaElement(graph, element));
    graphCache.set(video, graph);
    return graph;
  } catch {
    return null;
  }
}

function useLoudnessMeter(
  videoRef: RefObject<HTMLVideoElement | null> | undefined,
  isPlaying: boolean | undefined,
): MeterValues {
  const [values, setValues] = useState<MeterValues>(emptyMeter);

  useEffect(() => {
    if (!isPlaying || !videoRef?.current) {
      setValues(emptyMeter);
      return;
    }
    const video = videoRef.current;
    const connectAudioLayers = () => {
      const layers = Array.from(
        document.querySelectorAll<HTMLAudioElement>('audio.monitor-audio-layer'),
      );
      if (graphCache.has(video)) {
        const graph = graphCache.get(video);
        if (graph) layers.forEach((element) => connectMediaElement(graph, element));
      }
      return layers;
    };
    const graph = ensureGraph(video, connectAudioLayers());
    if (!graph) {
      setValues(emptyMeter);
      return;
    }
    const connectionPoll = window.setInterval(connectAudioLayers, 750);

    const { context, analyserLeft, analyserRight } = graph;
    const resume = () => {
      if (context.state === 'suspended') void context.resume().catch(() => undefined);
    };
    resume();
    window.addEventListener('pointerdown', resume, { passive: true });

    const leftData = new Uint8Array(new ArrayBuffer(analyserLeft.fftSize));
    const rightData = new Uint8Array(new ArrayBuffer(analyserRight.fftSize));
    let fast = DB_FLOOR;
    let slow = DB_FLOOR;
    let energy = 0;
    let energyFrames = 0;
    let peakLeft = DB_FLOOR;
    let peakRight = DB_FLOOR;
    let truePeak = DB_FLOOR;
    let frame = 0;
    let raf = 0;

    const readChannel = (analyser: AnalyserNode, data: Uint8Array<ArrayBuffer>) => {
      analyser.getByteTimeDomainData(data);
      let sum = 0;
      let peak = 0;
      for (const byte of data) {
        const sample = (byte - 128) / 128;
        sum += sample * sample;
        peak = Math.max(peak, Math.abs(sample));
      }
      return { rms: Math.sqrt(sum / data.length), peak };
    };

    const tick = () => {
      frame += 1;
      if (frame % 3 === 0) {
        const left = readChannel(analyserLeft, leftData);
        const rightSample = readChannel(analyserRight, rightData);
        const right = rightSample.rms < 0.00001 && left.rms > 0.00001 ? left : rightSample;
        const leftDb = clampDb(20 * Math.log10(Math.max(left.rms, 0.0001)));
        const rightDb = clampDb(20 * Math.log10(Math.max(right.rms, 0.0001)));
        fast = clampDb(fast * 0.55 + Math.max(leftDb, rightDb) * 0.45);
        slow = clampDb(slow * 0.94 + Math.max(leftDb, rightDb) * 0.06);
        const meanSquare = (left.rms * left.rms + right.rms * right.rms) / 2;
        energy += meanSquare;
        energyFrames += 1;
        peakLeft = Math.max(leftDb, peakLeft - 0.7);
        peakRight = Math.max(rightDb, peakRight - 0.7);
        truePeak = Math.max(
          truePeak,
          clampDb(20 * Math.log10(Math.max(left.peak, right.peak, 0.0001))),
        );
        setValues({
          left: leftDb,
          right: rightDb,
          peakLeft,
          peakRight,
          momentary: fast,
          shortTerm: slow,
          integrated: clampDb(10 * Math.log10(Math.max(energy / Math.max(1, energyFrames), 1e-8))),
          truePeak,
        });
      }
      raf = window.requestAnimationFrame(tick);
    };
    raf = window.requestAnimationFrame(tick);

    return () => {
      window.cancelAnimationFrame(raf);
      window.clearInterval(connectionPoll);
      window.removeEventListener('pointerdown', resume);
    };
  }, [isPlaying, videoRef]);

  return values;
}

function Meter({ level }: { level: number }) {
  return (
    <span className="meter" aria-hidden="true">
      {Array.from({ length: 12 }, (_, index) => (
        <span
          key={index}
          className={index < Math.round((level / 100) * 12) ? 'meter-lit' : 'meter-off'}
        />
      ))}
    </span>
  );
}

const meterTicks = Array.from({ length: 20 }, (_, index) => -index * 3);

function LoudnessMeter({
  videoRef,
  isPlaying,
}: {
  videoRef?: RefObject<HTMLVideoElement | null>;
  isPlaying?: boolean;
}) {
  const values = useLoudnessMeter(videoRef, isPlaying);
  const height = (db: number) => `${Math.max(0, Math.min(100, ((db - DB_FLOOR) / -DB_FLOOR) * 100))}%`;
  return (
    <div
      className="loudness-meter"
      aria-label="主输出实时电平"
      title="实时峰值电平（dBFS，近似响度读数；非标准 LUFS 测量）"
    >
      <div className="loudness-meter-body">
        <div className="loudness-bars">
          {[
            { key: 'left', label: 'L', level: values.left, peak: values.peakLeft },
            { key: 'right', label: 'R', level: values.right, peak: values.peakRight },
          ].map((channel) => (
            <div className="loudness-channel" key={channel.key}>
              <div className="loudness-track">
                <span className="loudness-fill" style={{ height: height(channel.level) }} />
                <span className="loudness-peak" style={{ bottom: height(channel.peak) }} />
              </div>
              <span className="loudness-channel-label">{channel.label}</span>
            </div>
          ))}
        </div>
        <div className="loudness-scale" aria-hidden="true">
          {meterTicks.map((tick) => (
            <span key={tick}>{tick}</span>
          ))}
        </div>
      </div>
      <div className="loudness-stats" aria-label="响度读数">
        <span><b>M</b><em>{formatDb(values.momentary)}</em></span>
        <span><b>S</b><em>{formatDb(values.shortTerm)}</em></span>
        <span><b>I</b><em>{formatDb(values.integrated)}</em></span>
        <span><b>TP</b><em>{formatDb(values.truePeak)}</em></span>
      </div>
    </div>
  );
}

export function AudioMixer({
  voiceVolume,
  musicVolume,
  effectVolume,
  masterVolume,
  videoRef,
  isPlaying,
  onChange,
}: AudioMixerProps) {
  const [focus, setFocus] = useState<Channel['id'] | 'master'>('master');
  const channels: Channel[] = [
    { id: 'voice', label: '对白', volume: voiceVolume },
    { id: 'music', label: '音乐', volume: musicVolume },
    { id: 'effect', label: '音效', volume: effectVolume },
  ];
  const average = (voiceVolume + musicVolume + effectVolume) / 3;
  const tabOrder: Array<Channel['id'] | 'master'> = ['voice', 'music', 'effect', 'master'];
  const tabRefs = useRef<Array<HTMLButtonElement | null>>([]);

  // role=tablist 的 roving tabindex：←/→ 循环、Home/End 跳到首尾（与设计层 SegmentedTabs 一致）。
  const handleTabKeyDown = (event: ReactKeyboardEvent<HTMLButtonElement>, index: number) => {
    const last = tabOrder.length - 1;
    let next = -1;
    if (event.key === 'ArrowRight' || event.key === 'ArrowDown') {
      next = index === last ? 0 : index + 1;
    } else if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') {
      next = index === 0 ? last : index - 1;
    } else if (event.key === 'Home') {
      next = 0;
    } else if (event.key === 'End') {
      next = last;
    }
    if (next < 0) return;
    event.preventDefault();
    setFocus(tabOrder[next]);
    tabRefs.current[next]?.focus();
  };

  return (
    <section className="panel audio-panel">
      <div className="panel-head">
        <span className="panel-tab is-active">音频混合器</span>
      </div>
      <div className="audio-content">
        <div className="audio-tabs" role="tablist" aria-label="音频通道">
          {channels.map((channel, index) => (
            <button
              key={channel.id}
              ref={(element) => {
                tabRefs.current[index] = element;
              }}
              type="button"
              role="tab"
              aria-selected={focus === channel.id}
              tabIndex={focus === channel.id ? 0 : -1}
              className={focus === channel.id ? 'is-active' : ''}
              onClick={() => setFocus(channel.id)}
              onKeyDown={(event) => handleTabKeyDown(event, index)}
            >
              {channel.label}
            </button>
          ))}
          <button
            ref={(element) => {
              tabRefs.current[tabOrder.length - 1] = element;
            }}
            type="button"
            role="tab"
            aria-selected={focus === 'master'}
            tabIndex={focus === 'master' ? 0 : -1}
            className={focus === 'master' ? 'is-active' : ''}
            onClick={() => setFocus('master')}
            onKeyDown={(event) => handleTabKeyDown(event, tabOrder.length - 1)}
          >
            主输出
          </button>
        </div>
        <div className="mixer-body">
          <div className="mixer-strips">
            {channels.map((channel) => (
              <div
                key={channel.id}
                className={`mixer-strip${focus === channel.id ? ' is-focused' : ''}`}
                onFocus={() => setFocus(channel.id)}
              >
                <span className="strip-label">{channel.label}</span>
                <input
                  className="fader"
                  type="range"
                  min="0"
                  max="200"
                  step="1"
                  value={Math.round(channel.volume * 100)}
                  aria-label={`${channel.label}音量`}
                  onChange={(event) => onChange(channel.id, Number(event.target.value) / 100)}
                />
                <span className="fader-value" title={dbLabel(channel.volume)} aria-label={`${channel.label} ${dbLabel(channel.volume)}`}>
                  {dbValue(channel.volume)}
                </span>
                <Meter level={Math.min(100, channel.volume * 50)} />
              </div>
            ))}
            <div
              className={`mixer-strip master-strip${focus === 'master' ? ' is-focused' : ''}`}
              onFocus={() => setFocus('master')}
            >
              <span className="strip-label">主输出</span>
              <input
                className="fader fader-master"
                type="range"
                min="0"
                max="200"
                step="1"
                value={Math.round(masterVolume * 100)}
                aria-label="主输出音量"
                title={`主输出 ${dbLabel(masterVolume)}`}
                onChange={(event) => onChange('master', Number(event.target.value) / 100)}
              />
              <span className="fader-value master-value" title={dbLabel(masterVolume)} aria-label={`主输出 ${dbLabel(masterVolume)}`}>
                {dbValue(masterVolume)}
              </span>
              <Meter level={Math.min(100, average * masterVolume * 50)} />
            </div>
          </div>
          <LoudnessMeter videoRef={videoRef} isPlaying={isPlaying} />
        </div>
        <div className="audio-footer">
          <span>{dbLabel(masterVolume)}</span>
          <span>{channels.length} 个通道</span>
          <span>实时电平</span>
          <span className="mixer-unit">读数单位 dB</span>
        </div>
      </div>
    </section>
  );
}
