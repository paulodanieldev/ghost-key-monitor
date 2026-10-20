// Testes do GhostKeyDetector (console, sem hardware). Compilados e executados por build.ps1.
using System;
using System.Collections.Generic;
using GhostKey;

static class DetectorTests
{
    const int A = 0x41;
    static int passed, failed;

    static GhostKeyDetector Make(bool noGrace)
    {
        var d = new GhostKeyDetector(() => 0.0);
        if (noGrace) d.StartupGrace = 0;
        return d;
    }

    static GhostInfo Ev(GhostKeyDetector d, string type, double t)
    {
        return d.Analyze(A, type == "down", t);
    }

    static void Check(string name, bool ok)
    {
        if (ok) { passed++; Console.WriteLine("  ok    " + name); }
        else { failed++; Console.WriteLine("  FALHOU " + name); }
    }

    static bool Pattern(GhostInfo g, string p, string sev)
    {
        return g != null && g.Pattern == p && g.Severity == sev;
    }

    static int Main()
    {
        GhostKeyDetector d; GhostInfo g;

        d = Make(true);
        Check("digitacao normal: down/up limpos", Ev(d, "down", 1000) == null && Ev(d, "up", 1100) == null);

        d = Make(true);
        Check("dois toques normais sao limpos",
            Ev(d, "down", 1000) == null && Ev(d, "up", 1090) == null &&
            Ev(d, "down", 1250) == null && Ev(d, "up", 1340) == null);

        d = Make(true);
        bool clean = Ev(d, "down", 1000) == null;
        for (int i = 0; i < 30; i++) clean &= Ev(d, "down", 1500 + 33 * i) == null; // auto-repeat do Windows
        clean &= Ev(d, "up", 2600) == null;
        Check("segurar tecla (auto-repeat) nao e fantasma", clean);

        // Caso real do usuario: dupla batida rapida (soltar->apertar em ~27ms) NAO pode acusar.
        d = Make(true);
        bool dbl = Ev(d, "down", 1000) == null && Ev(d, "up", 1080) == null &&
                   Ev(d, "down", 1107) == null && Ev(d, "up", 1180) == null;
        Check("dupla batida rapida (27ms) nao acusa", dbl);

        d = Make(true);
        bool four = Ev(d, "down", 1000) == null;
        for (int i = 0; i < 4; i++)  // 4 toques rapidos (UP->DOWN em 30ms): abaixo da recorrencia de 5
        {
            four &= Ev(d, "up", 1000 + 150 + 180 * i) == null;
            four &= Ev(d, "down", 1000 + 180 + 180 * i) == null;
        }
        Check("4 toques rapidos seguidos nao acusam (recorrencia padrao = 5)", four && d.RapidRepeatThreshold == 5);

        d = Make(true);
        Ev(d, "down", 1000);
        GhostInfo fifth = null; int firstHit = -1;
        for (int i = 0; i < 5; i++)  // cada ciclo: solta e aperta de novo em 30ms
        {
            Ev(d, "up", 1000 + 150 + 180 * i);
            GhostInfo r = Ev(d, "down", 1000 + 180 + 180 * i);
            if (r != null && firstHit < 0) { firstHit = i; fifth = r; }
        }
        Check("RAPID_REPEAT: acusa no 5o toque rapido recorrente", firstHit == 4 && Pattern(fifth, "RAPID_REPEAT", "ALTO"));

        d = Make(true);
        GhostInfo pulse = null; int pulseHit = -1;
        for (int i = 0; i < 5; i++)  // pulsos fantasma de 5ms
        {
            Ev(d, "down", 1000 + 300 * i);
            GhostInfo r = Ev(d, "up", 1005 + 300 * i);
            if (r != null && pulseHit < 0) { pulseHit = i; pulse = r; }
        }
        Check("RAPID_REPEAT: 5 pulsos DOWN->UP de 5ms acusam", pulseHit == 4 && Pattern(pulse, "RAPID_REPEAT", "ALTO"));

        d = Make(true);
        bool spec = false;   // timeline da especificacao 5/3/4ms, repetida
        for (int i = 0; i < 6; i++)
        {
            double b = 15 * i;
            spec |= Ev(d, "down", b) != null;
            spec |= Ev(d, "up", b + 5) != null;
            spec |= Ev(d, "down", b + 8) != null;
            spec |= Ev(d, "up", b + 12) != null;
        }
        Check("timeline da especificacao 5/3/4ms repetida e detectada", spec);

        d = Make(true);
        d.RapidRepeatThreshold = 2;
        Ev(d, "down", 1000); Ev(d, "up", 1080);
        Check("recorrencia configuravel: com 2, a dupla batida rapida acusa", Ev(d, "down", 1107) != null);

        d = Make(true);
        Ev(d, "down", 1000); Ev(d, "up", 1080); Ev(d, "down", 1107);
        bool decays = true;
        for (int i = 0; i < 4; i++)  // toques rapidos espalhados por mais de 2s nao se acumulam
        {
            double t0 = 5000 + 3000 * i;
            Ev(d, "down", t0 + 500); Ev(d, "up", t0 + 590);
            decays &= Ev(d, "down", t0 + 617) == null;
        }
        Check("toques rapidos espacados (> 2s) nao se acumulam", decays);

        d = Make(true);
        Ev(d, "down", 1000); g = Ev(d, "down", 1080);
        Check("DOUBLE_DOWN e CRITICO", Pattern(g, "DOUBLE_DOWN", "CRÍTICO"));

        d = Make(true);
        Ev(d, "down", 1000); g = Ev(d, "down", 1010);
        Check("padrao mais grave vence", g != null && g.Pattern == "DOUBLE_DOWN");

        d = Make(true);
        g = Ev(d, "up", 1000);
        Check("ORPHAN_UP", Pattern(g, "ORPHAN_UP", "ALTO"));

        d = Make(false); // carencia padrao de 3000ms
        Check("ORPHAN_UP ignorado logo apos iniciar", Ev(d, "up", 1000) == null);
        Check("ORPHAN_UP detectado depois da carencia", Pattern(Ev(d, "up", 5000), "ORPHAN_UP", "ALTO"));

        d = Make(true);
        var res = new List<GhostInfo>();
        double t = 1000;
        for (int i = 0; i < 5; i++) { res.Add(Ev(d, "down", t)); res.Add(Ev(d, "up", t + 60)); t += 120; }
        int bursts = 0; GhostInfo burst = null;
        foreach (var r in res) if (r != null && r.Pattern == "BURST") { bursts++; burst = r; }
        Check("BURST: 5 toques em 500ms", bursts == 1 && burst.Severity == "MÉDIO");

        d = Make(true);
        bool fourOk = true;
        t = 1000;
        for (int i = 0; i < 4; i++) { fourOk &= Ev(d, "down", t) == null; fourOk &= Ev(d, "up", t + 60) == null; t += 120; }
        Check("BURST: 4 toques em 500ms nao acusam (padrao 5)", fourOk);

        d = Make(true);
        bool slow = true;
        for (int i = 0; i < 6; i++)
        {
            slow &= Ev(d, "down", 1000 + i * 300) == null;
            slow &= Ev(d, "up", 1000 + i * 300 + 90) == null;
        }
        Check("toques lentos repetidos nao disparam BURST", slow);

        d = Make(true);
        d.DisableKeyTemporarily(A);
        bool dis = d.IsKeyDisabled(A) && Ev(d, "up", 1000) == null;
        d.EnableKey(A);
        dis &= !d.IsKeyDisabled(A) && Ev(d, "up", 2000) != null;
        Check("desativar e reativar tecla", dis);

        d = Make(true);
        d.DisableKeyTemporarily(A);
        Check("outras teclas nao sao afetadas", d.Analyze(0x42, false, 1000) != null);

        d = Make(true);
        Ev(d, "down", 1000);
        bool newPress = d.LastWasNewPress;
        Ev(d, "down", 1600);
        Check("LastWasNewPress distingue auto-repeat", newPress && !d.LastWasNewPress);

        Check("nomes de tecla",
            KeyNames.Get(0x41) == "A" && KeyNames.Get(0x10) == "Shift" &&
            KeyNames.Get(0x70) == "F1" && KeyNames.Get(0xFF) == "VK_0xFF");

        // ---- PADRAO 5: STUCK_KEY (tecla presa) ----
        Func<int, bool> stillDown = k => true;

        d = Make(true);
        Ev(d, "down", 1000);
        Check("STUCK: 4s segurada ainda nao e fantasma", d.CheckStuck(5000, stillDown) == null);
        g = d.CheckStuck(6500, stillDown);
        Check("STUCK: 5,5s sem KEY_UP e CRITICO", Pattern(g, "STUCK_KEY", "CRÍTICO"));
        Check("STUCK: so avisa uma vez por pressionamento", d.CheckStuck(9000, stillDown) == null);

        d = Make(true);
        Ev(d, "down", 1000); Ev(d, "up", 2000); Ev(d, "down", 3000);
        Check("STUCK: tecla solta e apertada de novo nao acusa", d.CheckStuck(6000, stillDown) == null);

        d = Make(true);
        Ev(d, "down", 1000);
        for (int i = 0; i < 40; i++) Ev(d, "down", 1500 + 33 * i);  // segurando: auto-repeat
        Check("STUCK: tecla segurada com auto-repeat e detectada", d.CheckStuck(8000, stillDown) != null);

        d = Make(true);
        Ev(d, "down", 1000);
        Check("STUCK: estado velho (Windows diz que soltou) e descartado",
            d.CheckStuck(9000, k => false) == null && d.CheckStuck(9500, stillDown) == null);

        d = Make(true);
        d.Analyze(0x10, true, 1000); // Shift
        bool shiftOk = d.CheckStuck(10000, stillDown) == null && d.CheckStuck(22000, stillDown) != null;
        Check("STUCK: modificadores tem limite maior (20s)", shiftOk);

        d = Make(true);
        Ev(d, "down", 1000);
        d.ClearPressState();
        Check("ClearPressState esquece teclas pressionadas", d.CheckStuck(9000, stillDown) == null);

        d = Make(true);
        Ev(d, "down", 1000);
        d.DisableKeyTemporarily(A);
        Check("STUCK: tecla desativada nao acusa", d.CheckStuck(9000, stillDown) == null);

        Console.WriteLine();
        Console.WriteLine("{0} passaram, {1} falharam", passed, failed);
        return failed == 0 ? 0 : 1;
    }
}
