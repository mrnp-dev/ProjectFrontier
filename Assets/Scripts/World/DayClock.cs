using System;
using UnityEngine;

namespace ProjectFrontier.World
{
    /// <summary>The four daily phases driving lighting, NPC schedules, and evening gossip exchange.</summary>
    public enum DayPhase
    {
        Morning = 0,
        Midday = 1,
        Evening = 2,
        Night = 3
    }

    /// <summary>
    /// Pure-C# day/night clock for Oakhaven (06:00 -> 24:00 across Morning, Midday, Evening, Night).
    /// Zero per-frame allocations; raises events only on phase transitions, midnight, and day rollover.
    /// </summary>
    public sealed class DayClock
    {
        public const float StartHour = 6f;
        public const float EndHour = 24f;
        public const float WakingHours = EndHour - StartHour; // 18 in-game hours

        // Normalized boundaries corresponding to 06:00, 11:00, 17:00, 21:00, 24:00.
        public const float MiddayStart = 5f / WakingHours;   // 11:00 (~0.2778)
        public const float EveningStart = 11f / WakingHours; // 17:00 (~0.6111)
        public const float NightStart = 15f / WakingHours;   // 21:00 (~0.8333)

        public const float DefaultDayDurationSeconds = 180f;

        public int Day { get; private set; } = 1;
        public float NormalizedTime { get; private set; }
        public DayPhase CurrentPhase { get; private set; } = DayPhase.Morning;
        public float DayDurationSeconds { get; set; } = DefaultDayDurationSeconds;
        public bool MidnightPending { get; private set; }

        public event Action<DayPhase, DayPhase> PhaseChanged;
        public event Action<int> DayAdvanced;
        public event Action MidnightReached;

        public DayClock(float dayDurationSeconds = DefaultDayDurationSeconds, int startDay = 1)
        {
            DayDurationSeconds = Mathf.Max(4f, dayDurationSeconds);
            Day = Math.Max(1, startDay);
            NormalizedTime = 0f;
            CurrentPhase = DayPhase.Morning;
        }

        /// <summary>Syncs the day number from persistent SocialState without firing DayAdvanced.</summary>
        public void SyncDay(int day)
        {
            Day = Math.Max(1, day);
        }

        /// <summary>Advances the clock by real seconds. Fires PhaseChanged and MidnightReached as crossed.</summary>
        public void Tick(float deltaSeconds)
        {
            if (deltaSeconds <= 0f || DayDurationSeconds <= 0.001f) return;

            float next = NormalizedTime + (deltaSeconds / DayDurationSeconds);
            if (next >= 1f)
            {
                NormalizedTime = 1f;
                UpdatePhase();
                if (!MidnightPending)
                {
                    MidnightPending = true;
                    MidnightReached?.Invoke();
                }
                return;
            }

            NormalizedTime = next;
            UpdatePhase();
        }

        /// <summary>Jumps directly to a normalized time in [0, 1]. Useful for verification and debug controls.</summary>
        public void SetNormalizedTime(float normalized)
        {
            NormalizedTime = Mathf.Clamp01(normalized);
            MidnightPending = NormalizedTime >= 1f;
            UpdatePhase();
        }

        /// <summary>Jumps to the start of the requested phase.</summary>
        public void SetPhase(DayPhase phase)
        {
            SetNormalizedTime(GetPhaseStart(phase));
        }

        /// <summary>
        /// Transitions to Morning (06:00) of the next day. Pass <paramref name="syncedDay"/> when
        /// SocialState has already incremented the day so both stay locked in step.
        /// </summary>
        public void SleepToNextDay(int? syncedDay = null)
        {
            Day = syncedDay.HasValue ? Math.Max(1, syncedDay.Value) : Day + 1;
            NormalizedTime = 0f;
            MidnightPending = false;
            UpdatePhase();
            DayAdvanced?.Invoke(Day);
        }

        public static DayPhase PhaseForNormalizedTime(float t)
        {
            t = Mathf.Clamp01(t);
            if (t < MiddayStart) return DayPhase.Morning;
            if (t < EveningStart) return DayPhase.Midday;
            if (t < NightStart) return DayPhase.Evening;
            return DayPhase.Night;
        }

        public static float GetPhaseStart(DayPhase phase) => phase switch
        {
            DayPhase.Morning => 0f,
            DayPhase.Midday => MiddayStart,
            DayPhase.Evening => EveningStart,
            DayPhase.Night => NightStart,
            _ => 0f
        };

        public static float GetPhaseMidpoint(DayPhase phase) => phase switch
        {
            DayPhase.Morning => MiddayStart * 0.5f,
            DayPhase.Midday => (MiddayStart + EveningStart) * 0.5f,
            DayPhase.Evening => (EveningStart + NightStart) * 0.5f,
            DayPhase.Night => (NightStart + 1f) * 0.5f,
            _ => 0f
        };

        /// <summary>Returns the in-game hour (6..24) and 10-minute step (0, 10, ..., 50).</summary>
        public void GetClockTime(out int hour, out int minute10)
        {
            float totalMinutes = NormalizedTime * WakingHours * 60f;
            int snapped = Mathf.FloorToInt(totalMinutes / 10f) * 10;
            int totalFromMidnight = (int)StartHour * 60 + snapped;
            hour = Mathf.Clamp(totalFromMidnight / 60, 6, 24);
            minute10 = hour >= 24 ? 0 : totalFromMidnight % 60;
        }

        public static string PhaseLabel(DayPhase phase) => phase switch
        {
            DayPhase.Morning => "MORNING",
            DayPhase.Midday => "MIDDAY",
            DayPhase.Evening => "EVENING",
            DayPhase.Night => "NIGHT",
            _ => "MORNING"
        };

        public static string PhasePromptWord(DayPhase phase) => phase switch
        {
            DayPhase.Morning => "Morning",
            DayPhase.Midday => "Midday",
            DayPhase.Evening => "Evening",
            DayPhase.Night => "Night",
            _ => "Morning"
        };

        public string FormatHudLabel()
        {
            GetClockTime(out int hour, out int minute10);
            int displayHour = hour >= 24 ? 0 : hour;
            return $"DAY {Day} · {PhaseLabel(CurrentPhase)} · {displayHour:00}:{minute10:00}";
        }

        private void UpdatePhase()
        {
            DayPhase next = PhaseForNormalizedTime(NormalizedTime);
            if (next != CurrentPhase)
            {
                DayPhase prev = CurrentPhase;
                CurrentPhase = next;
                PhaseChanged?.Invoke(prev, next);
            }
        }
    }
}
