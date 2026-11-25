using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Synthesizes procedural PCM audio clips (pickup blip, milestone sting, lose sting, ambient bed, UI click)
    /// and routes all playback through a headless-compatible gain layer with pause ducking (-6 dB).
    /// </summary>
    public class SynthAudioManager : MonoBehaviour
    {
        public static SynthAudioManager Instance { get; private set; }

        private const int SampleRate = 22050;
        private const float BaseAmbientGain = 0.22f;
        private const float BaseSfxGain = 0.55f;

        private float _musicVolume = 0.8f;
        private float _sfxVolume = 0.85f;
        private float _pauseDuck = 1f;

        private AudioSource _ambientSource;
        private AudioSource _sfxSource;

        private AudioClip _pickupClip;
        private AudioClip _milestoneClip;
        private AudioClip _loseClip;
        private AudioClip _uiClickClip;
        private AudioClip _ambientClip;

        public float MusicVolume
        {
            get => _musicVolume;
            set
            {
                _musicVolume = Mathf.Clamp01(value);
                ApplyGains();
            }
        }

        public float SfxVolume
        {
            get => _sfxVolume;
            set
            {
                _sfxVolume = Mathf.Clamp01(value);
                ApplyGains();
            }
        }

        public AudioSource AmbientSource => _ambientSource;
        public AudioSource SfxSource => _sfxSource;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            _ambientSource = gameObject.AddComponent<AudioSource>();
            _ambientSource.loop = true;
            _ambientSource.playOnAwake = false;
            _ambientSource.spatialBlend = 0f;

            _sfxSource = gameObject.AddComponent<AudioSource>();
            _sfxSource.loop = false;
            _sfxSource.playOnAwake = false;
            _sfxSource.spatialBlend = 0f;

            GenerateClips();
            ApplyGains();

            _ambientSource.clip = _ambientClip;
            _ambientSource.Play();
        }

        public void SetPaused(bool paused)
        {
            // -6 dB duck (~0.5 linear gain) kept separate from slider gain so sliders never break ducking.
            _pauseDuck = paused ? 0.5f : 1f;
            ApplyGains();
        }

        private void ApplyGains()
        {
            if (_ambientSource != null)
            {
                _ambientSource.volume = BaseAmbientGain * _musicVolume * _pauseDuck;
            }
            if (_sfxSource != null)
            {
                _sfxSource.volume = BaseSfxGain * _sfxVolume;
            }
        }

        public void PlayPickup()
        {
            if (_sfxSource == null || _pickupClip == null) return;
            _sfxSource.pitch = Random.Range(0.92f, 1.08f);
            _sfxSource.PlayOneShot(_pickupClip, 1f);
        }

        public void PlayMilestone()
        {
            if (_sfxSource == null || _milestoneClip == null) return;
            _sfxSource.pitch = 1f;
            _sfxSource.PlayOneShot(_milestoneClip, 1f);
        }

        public void PlayLose()
        {
            if (_sfxSource == null || _loseClip == null) return;
            _sfxSource.pitch = 1f;
            _sfxSource.PlayOneShot(_loseClip, 1f);
        }

        public void PlayUiClick()
        {
            if (_sfxSource == null || _uiClickClip == null) return;
            _sfxSource.pitch = Random.Range(0.96f, 1.04f);
            _sfxSource.PlayOneShot(_uiClickClip, 0.7f);
        }

        private void GenerateClips()
        {
            _pickupClip = CreatePickupBlip();
            _milestoneClip = CreateTriadSting("MilestoneSting", new[] { 523.25f, 659.25f, 783.99f });
            _loseClip = CreateTriadSting("LoseSting", new[] { 392.00f, 349.23f, 293.66f });
            _uiClickClip = CreateUiClick();
            _ambientClip = CreateAmbientBed();
        }

        private static AudioClip CreatePickupBlip()
        {
            int len = Mathf.RoundToInt(SampleRate * 0.095f);
            float[] data = new float[len];
            float phase = 0f;
            for (int i = 0; i < len; i++)
            {
                float u = (float)i / len;
                float freq = Mathf.Lerp(880f, 1320f, u);
                phase += 2f * Mathf.PI * freq / SampleRate;
                float decay = Mathf.Pow(1f - u, 1.6f);
                data[i] = Mathf.Sin(phase) * decay * 0.75f;
            }
            ApplyEdgeFade(data, 0.005f);
            var clip = AudioClip.Create("PickupBlip", len, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip CreateTriadSting(string name, float[] freqs)
        {
            float noteDur = 0.14f;
            float step = 0.10f;
            float totalDur = step * (freqs.Length - 1) + noteDur + 0.05f;
            int len = Mathf.RoundToInt(SampleRate * totalDur);
            float[] data = new float[len];

            for (int n = 0; n < freqs.Length; n++)
            {
                int start = Mathf.RoundToInt(n * step * SampleRate);
                int nLen = Mathf.RoundToInt(noteDur * SampleRate);
                float freq = freqs[n];
                for (int i = 0; i < nLen && start + i < len; i++)
                {
                    float u = (float)i / nLen;
                    float env = Mathf.Sin(Mathf.Min(u * 10f, 1f) * Mathf.PI * 0.5f) * Mathf.Pow(1f - u, 1.3f);
                    float t = (float)i / SampleRate;
                    float s = Mathf.Sin(2f * Mathf.PI * freq * t) * 0.65f + Mathf.Sin(2f * Mathf.PI * freq * 2f * t) * 0.18f;
                    data[start + i] += s * env * 0.45f;
                }
            }

            ApplyEdgeFade(data, 0.005f);
            var clip = AudioClip.Create(name, len, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip CreateUiClick()
        {
            int len = Mathf.RoundToInt(SampleRate * 0.022f);
            float[] data = new float[len];
            System.Random rng = new System.Random(1337);
            float lp = 0f;
            for (int i = 0; i < len; i++)
            {
                float u = (float)i / len;
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp = Mathf.Lerp(lp, white, 0.32f);
                float ping = Mathf.Sin(2f * Mathf.PI * 1100f * i / SampleRate) * 0.35f;
                data[i] = (lp * 0.65f + ping) * (1f - u);
            }
            ApplyEdgeFade(data, 0.004f);
            var clip = AudioClip.Create("UiClick", len, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip CreateAmbientBed()
        {
            int len = SampleRate * 5; // 5-second seamless loop
            float[] data = new float[len];
            System.Random rng = new System.Random(4242);
            float brown = 0f;
            for (int i = 0; i < len; i++)
            {
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                brown = Mathf.Clamp(brown * 0.985f + white * 0.035f, -0.6f, 0.6f);
                float t = (float)i / SampleRate;
                float swell = Mathf.Sin(2f * Mathf.PI * 0.2f * t) * 0.5f + 0.5f;
                float pad = (Mathf.Sin(2f * Mathf.PI * 146.83f * t) + Mathf.Sin(2f * Mathf.PI * 220.0f * t) * 0.5f) * 0.08f * (0.5f + 0.5f * swell);
                data[i] = brown * 0.45f + pad;
            }
            // Crossfade tail into head for seamless looping
            int xfade = Mathf.RoundToInt(SampleRate * 0.15f);
            for (int i = 0; i < xfade; i++)
            {
                float a = (float)i / xfade;
                data[i] = Mathf.Lerp(data[len - xfade + i], data[i], a);
            }
            var clip = AudioClip.Create("VillageAmbientBed", len, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static void ApplyEdgeFade(float[] data, float seconds)
        {
            int fadeSamples = Mathf.Min(data.Length / 2, Mathf.Max(1, Mathf.RoundToInt(SampleRate * seconds)));
            for (int i = 0; i < fadeSamples; i++)
            {
                float g = (float)i / fadeSamples;
                data[i] *= g;
                data[data.Length - 1 - i] *= g;
            }
        }
    }
}
