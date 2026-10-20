// Detector de toque fantasma (ghost key). Sem dependencias de UI: pode ser testado sozinho.
// Compativel com C# 5 (csc.exe que ja vem no Windows).
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GhostKey
{
    public class GhostInfo
    {
        public int Key;
        public string KeyName;
        public string Pattern;     // RAPID_REPEAT | DOUBLE_DOWN | ORPHAN_UP | BURST
        public string Severity;    // CRÍTICO | ALTO | MÉDIO
        public string Detail;
        public double IntervalMs;
        public DateTime Time;
    }

    public static class KeyNames
    {
        static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            {0x08,"Backspace"},{0x09,"Tab"},{0x0D,"Enter"},{0x10,"Shift"},{0x11,"Ctrl"},{0x12,"Alt"},
            {0x13,"Pause"},{0x14,"Caps Lock"},{0x1B,"Esc"},{0x20,"Espaço"},{0x21,"Page Up"},
            {0x22,"Page Down"},{0x23,"End"},{0x24,"Home"},{0x25,"Seta Esquerda"},{0x26,"Seta Cima"},
            {0x27,"Seta Direita"},{0x28,"Seta Baixo"},{0x2C,"Print Screen"},{0x2D,"Insert"},
            {0x2E,"Delete"},{0x5B,"Win Esquerda"},{0x5C,"Win Direita"},{0x5D,"Menu"},
            {0x6A,"Num *"},{0x6B,"Num +"},{0x6D,"Num -"},{0x6E,"Num ,"},{0x6F,"Num /"},
            {0x90,"Num Lock"},{0x91,"Scroll Lock"},{0xA0,"Shift Esquerdo"},{0xA1,"Shift Direito"},
            {0xA2,"Ctrl Esquerdo"},{0xA3,"Ctrl Direito"},{0xA4,"Alt Esquerdo"},{0xA5,"Alt Direito"},
            {0xBA,";"},{0xBB,"="},{0xBC,","},{0xBD,"-"},{0xBE,"."},{0xBF,"/"},{0xC0,"`"},
            {0xDB,"["},{0xDC,"\\"},{0xDD,"]"},{0xDE,"'"}
        };

        public static string Get(int vk)
        {
            string name;
            if (Names.TryGetValue(vk, out name)) return name;
            if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            return "VK_0x" + vk.ToString("X2");
        }
    }

    /// <summary>
    /// Analisa eventos de tecla (down/up). Teclas identificadas pelo virtual-key code.
    /// Tempos em milissegundos (relogio monotonico). Thread-safe.
    /// </summary>
    public class GhostKeyDetector
    {
        // ---- Configuracoes de deteccao --------------------------------------
        public double GhostThreshold = 50;        // PADRAO 1: UP->DOWN menor que isso (ms)
        public double MinInterval = 0;            // intervalos menores que isso sao ignorados
        public double GhostDwell = 20;            // PADRAO 1: DOWN->UP menor que isso (ms)
        // RECORRENCIA MINIMA: quantas repeticoes suspeitas da mesma tecla sao necessarias para acusar.
        // Vale para o PADRAO 1 (toques rapidos dentro de RecurrenceWindow) e para o PADRAO 4
        // (pressionamentos dentro de TimeWindow). Um toque rapido isolado - ex.: dupla batida - nao acusa.
        public int RapidRepeatThreshold = 5;
        public double RecurrenceWindow = 2000;    // PADRAO 1: janela para contar os toques rapidos (ms)
        public double TimeWindow = 500;           // PADRAO 4: janela (ms)
        public double HistoryWindow = 2000;       // historico mantido por tecla (ms)
        public double AutoRepeatMinDelay = 200;   // KEY_DOWN repetido depois disso = auto-repeat do Windows
        public double StartupGrace = 3000;        // KEY_UP orfao nos primeiros ms e normal
        public double StuckThreshold = 5000;      // PADRAO 5: tecla "segurada" sem KEY_UP por tanto tempo (ms)
        public double StuckThresholdModifier = 20000; // idem para Shift/Ctrl/Alt/Win (segurar e comum)

        struct Ev { public bool Down; public double Time; }

        readonly object sync = new object();
        readonly Func<double> clock;
        readonly double started;
        readonly Dictionary<int, double> lastEventTime = new Dictionary<int, double>();
        readonly Dictionary<int, bool> lastEventWasDown = new Dictionary<int, bool>();
        readonly Dictionary<int, double> pressStack = new Dictionary<int, double>();
        readonly Dictionary<int, Queue<Ev>> history = new Dictionary<int, Queue<Ev>>();
        readonly HashSet<int> disabledKeys = new HashSet<int>();
        readonly HashSet<int> stuckReported = new HashSet<int>();
        readonly Dictionary<int, Queue<double>> rapidHits = new Dictionary<int, Queue<double>>();

        /// <summary>True se o ultimo KEY_DOWN analisado foi um pressionamento novo (nao auto-repeat).</summary>
        public bool LastWasNewPress;

        public GhostKeyDetector() : this(null) { }

        public GhostKeyDetector(Func<double> clockMs)
        {
            clock = clockMs ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
            started = clock();
        }

        public bool IsKeyDisabled(int key)
        {
            lock (sync) { return disabledKeys.Contains(key); }
        }

        public int[] DisabledKeys()
        {
            lock (sync)
            {
                int[] arr = new int[disabledKeys.Count];
                disabledKeys.CopyTo(arr);
                Array.Sort(arr);
                return arr;
            }
        }

        /// <summary>Desativa a tecla ate reiniciar (o estado vive so na memoria).</summary>
        public void DisableKeyTemporarily(int key)
        {
            lock (sync)
            {
                disabledKeys.Add(key);
                rapidHits.Remove(key);
                pressStack.Remove(key);
                history.Remove(key);
                lastEventTime.Remove(key);
                lastEventWasDown.Remove(key);
                stuckReported.Remove(key);
            }
        }

        /// <summary>Esquece as teclas "pressionadas" (ex.: apos bloquear/desbloquear a sessao,
        /// quando o Windows nao entrega os KEY_UP).</summary>
        public void ClearPressState()
        {
            lock (sync)
            {
                pressStack.Clear();
                stuckReported.Clear();
            }
        }

        static bool IsModifier(int vk)
        {
            return (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
        }

        /// <summary>
        /// PADRAO 5 (STUCK_KEY): tecla pressionada ha muito tempo sem KEY_UP - o sintoma tipico de
        /// tecla "presa". Chamar periodicamente (ex.: 1x por segundo). isPhysicallyDown confirma no
        /// Windows que a tecla ainda esta pressionada; se disser que nao, o estado era velho e e descartado.
        /// </summary>
        public GhostInfo CheckStuck(double now, Func<int, bool> isPhysicallyDown)
        {
            lock (sync)
            {
                GhostInfo best = null;
                List<int> stale = new List<int>();
                foreach (KeyValuePair<int, double> kv in pressStack)
                {
                    int key = kv.Key;
                    if (stuckReported.Contains(key) || disabledKeys.Contains(key)) continue;
                    double held = now - kv.Value;
                    double limit = IsModifier(key) ? StuckThresholdModifier : StuckThreshold;
                    if (held < limit) continue;
                    if (isPhysicallyDown != null && !isPhysicallyDown(key)) { stale.Add(key); continue; }
                    stuckReported.Add(key);
                    GhostInfo g = Make(key, "STUCK_KEY", "CRÍTICO",
                        string.Format("pressionada há {0:0.0}s sem soltar", held / 1000.0), held);
                    if (best == null) best = g;
                }
                foreach (int key in stale) pressStack.Remove(key);
                return best;
            }
        }

        /// <summary>Esquece o estado "pressionada" de uma tecla (depois de soltá-la por software).</summary>
        public void ReleaseKeyState(int key)
        {
            lock (sync)
            {
                pressStack.Remove(key);
                stuckReported.Remove(key);
            }
        }

        public void EnableKey(int key)
        {
            lock (sync) { disabledKeys.Remove(key); }
        }

        public GhostInfo Analyze(int key, bool isDown)
        {
            return Analyze(key, isDown, clock());
        }

        /// <summary>Analisa um evento. Retorna info do ghost (o padrao mais grave) ou null.</summary>
        public GhostInfo Analyze(int key, bool isDown, double now)
        {
            lock (sync)
            {
                LastWasNewPress = false;
                if (disabledKeys.Contains(key)) return null;

                bool hadPrev = lastEventTime.ContainsKey(key);
                double prevTime = hadPrev ? lastEventTime[key] : 0;
                bool prevWasDown = hadPrev && lastEventWasDown[key];
                GhostInfo best = null;

                if (isDown)
                {
                    double pressedAt;
                    if (pressStack.TryGetValue(key, out pressedAt))
                    {
                        double held = now - pressedAt;
                        if (held >= AutoRepeatMinDelay) return null; // auto-repeat normal do Windows
                        // PADRAO 2: DOUBLE_DOWN
                        best = Pick(best, Make(key, "DOUBLE_DOWN", "CRÍTICO",
                            string.Format("2 KEY_DOWN sem KEY_UP em {0:0.0}ms", held), held));
                    }
                    else
                    {
                        LastWasNewPress = true;
                        pressStack[key] = now;
                        if (hadPrev && !prevWasDown)
                        {
                            double gap = now - prevTime;
                            if (gap >= MinInterval && gap < GhostThreshold)
                                // PADRAO 1: RAPID_REPEAT (UP -> DOWN) - so acusa se recorrente
                                best = Pick(best, RapidHit(key, now,
                                    string.Format("último UP->DOWN em {0:0.0}ms", gap), gap));
                        }
                    }
                }
                else
                {
                    double pressedAt;
                    if (pressStack.TryGetValue(key, out pressedAt))
                    {
                        pressStack.Remove(key);
                        stuckReported.Remove(key);
                        double dwell = now - pressedAt;
                        if (dwell >= MinInterval && dwell < GhostDwell)
                            // PADRAO 1: RAPID_REPEAT (DOWN -> UP) - so acusa se recorrente
                            best = Pick(best, RapidHit(key, now,
                                string.Format("último DOWN->UP em {0:0.0}ms", dwell), dwell));
                    }
                    else if (now - started >= StartupGrace)
                    {
                        // PADRAO 3: ORPHAN_UP
                        best = Pick(best, Make(key, "ORPHAN_UP", "ALTO", "KEY_UP sem KEY_DOWN previo", 0));
                    }
                }

                // Historico + PADRAO 4: BURST
                Queue<Ev> q;
                if (!history.TryGetValue(key, out q)) { q = new Queue<Ev>(); history[key] = q; }
                q.Enqueue(new Ev { Down = isDown, Time = now });
                while (q.Count > 0 && now - q.Peek().Time > HistoryWindow) q.Dequeue();
                if (isDown)
                {
                    int recent = 0;
                    foreach (Ev e in q) if (e.Down && now - e.Time <= TimeWindow) recent++;
                    if (recent >= RapidRepeatThreshold)
                        best = Pick(best, Make(key, "BURST", "MÉDIO",
                            string.Format("{0} pressionamentos em {1:0}ms", recent, TimeWindow), TimeWindow));
                }

                lastEventTime[key] = now;
                lastEventWasDown[key] = isDown;
                return best;
            }
        }

        /// <summary>Registra um toque rapido; acusa RAPID_REPEAT so quando ha RapidRepeatThreshold deles
        /// dentro de RecurrenceWindow. Chamar com o lock ja adquirido.</summary>
        GhostInfo RapidHit(int key, double now, string what, double value)
        {
            Queue<double> q;
            if (!rapidHits.TryGetValue(key, out q)) { q = new Queue<double>(); rapidHits[key] = q; }
            q.Enqueue(now);
            while (q.Count > 0 && now - q.Peek() > RecurrenceWindow) q.Dequeue();
            if (q.Count < RapidRepeatThreshold) return null;
            int n = q.Count;
            double span = now - q.Peek();
            q.Clear(); // precisa de novas ocorrencias para acusar de novo
            return Make(key, "RAPID_REPEAT", "ALTO",
                string.Format("{0} toques rápidos em {1:0}ms ({2})", n, span, what), value);
        }

        static int Rank(string severity)
        {
            return severity == "CRÍTICO" ? 3 : severity == "ALTO" ? 2 : 1;
        }

        static GhostInfo Pick(GhostInfo current, GhostInfo candidate)
        {
            if (candidate == null) return current;
            return current == null || Rank(candidate.Severity) > Rank(current.Severity) ? candidate : current;
        }

        static GhostInfo Make(int key, string pattern, string severity, string detail, double interval)
        {
            return new GhostInfo
            {
                Key = key, KeyName = KeyNames.Get(key), Pattern = pattern, Severity = severity,
                Detail = detail, IntervalMs = interval, Time = DateTime.Now
            };
        }
    }
}
