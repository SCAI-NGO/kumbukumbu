// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Kumbu.Core.Tests.Search;

/// <summary>Texts to measure the token bound with: ordinary prose, and kinds of text that take many tokens per character.</summary>
public static class DenseFixtures
{
    public const string GermanProse = """
        Die Kreiselpumpe P-200 fördert Kühlwasser aus dem Vorratsbehälter in den Wärmetauscher der Druckluftaufbereitung.
        Vor jeder Inbetriebnahme ist die Saugleitung vollständig zu entlüften, da Trockenlauf die Gleitringdichtung innerhalb weniger Sekunden zerstört.
        Die Förderhöhe beträgt bei Nenndrehzahl 42 Meter; größere Druckverluste in der Rohrleitungsführung verringern den Volumenstrom spürbar.
        Bei Störungsmeldungen des Motorschutzschalters muss zuerst die Ursache der Überlastung geklärt werden, bevor die Pumpe erneut eingeschaltet wird.
        Die Schmierfettfüllung der Wälzlager ist alle 4.000 Betriebsstunden zu erneuern; überschüssiges Fett gelangt sonst in die Wicklungsisolierung.
        Außerhalb der Heizperiode empfiehlt sich ein wöchentlicher Probelauf von fünf Minuten, damit sich die Laufradabdichtung nicht festsetzt.
        """;

    public const string NumericTable = """
        | Code | Register | Messwert | Grenze | Einheit |
        |------|----------|----------|--------|---------|
        | 0x1A3F | 0x00FF | 12.375 | 15.000 | bar |
        | 0x2B4E | 0x01FE | 0.0042 | 0.0050 | m³/s |
        | 0x3C5D | 0x02FD | 87.910 | 90.000 | °C |
        | 0x4D6C | 0x03FC | 1450.5 | 1500.0 | 1/min |
        | 0x5E7B | 0x04FB | 3.1416 | 4.0000 | kW |
        | 0x6F8A | 0x05FA | 230.01 | 240.00 | V |
        | 0x7099 | 0x06F9 | 16.004 | 20.000 | A |
        | 0x81A8 | 0x07F8 | 0.9876 | 1.0000 | cos φ |
        | 0x92B7 | 0x08F7 | 55.555 | 60.000 | dB(A) |
        | 0xA3C6 | 0x09F6 | 7.0710 | 8.0000 | mm/s |
        """;

    public const string Code = """
        static int pump_start(struct pump *p, uint16_t rpm) {
            if (p == NULL || rpm > PUMP_MAX_RPM) return -EINVAL;
            p->state = (p->state & ~STATE_MASK) | STATE_RUNNING;
            for (int i = 0; i < p->n_sensors; ++i) { p->sensors[i].last = read_adc(p->sensors[i].ch); }
            write_reg(p->base + 0x1C, (rpm << 4) ^ 0xA5A5);
            return p->fault ? -EIO : 0;
        }
        """;

    public const string Urls = """
        https://docs.example.org/manuals/pumps/p-200/de/inbetriebnahme/kapitel-4.html#abschnitt-4-2-entlueftung
        https://downloads.example.org/firmware/p-200/v3.14.159/release-notes.txt?lang=de&format=plain&rev=26535
        https://support.example.org/kb/articles/00042-gleitringdichtung-wechseln?ref=wartungsplan&utm_source=handbuch
        https://example.org/api/v2/devices/P-200-0x1A3F/telemetry?from=2026-01-01T00:00:00Z&to=2026-11-09T23:59:59Z
        """;

    public static readonly string Hex = Convert.ToHexStringLower(Bytes(400, seed: 1));

    public static readonly string Base64 = Convert.ToBase64String(Bytes(600, seed: 2));

    public static readonly string Punctuation = Repeat("!?.,;:-_()[]{}<>/\\|\"'`~^*+=&%$#@", 600);

    public static readonly string Symbols = Repeat("→∑≈€§¶•✓★♣", 600);

    /// <summary>The dense texts, each at least 500 characters long.</summary>
    public static IEnumerable<(string Name, string Text)> Dense =>
    [
        ("numeric table", Repeat(NumericTable, 600)),
        ("base64", Base64),
        ("punctuation", Punctuation),
        ("symbols", Symbols),
        ("hex", Hex),
    ];

    private static byte[] Bytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Repeat(string unit, int length)
    {
        var text = new StringBuilder(length + unit.Length);
        while (text.Length < length) text.Append(unit);
        return text.ToString(0, length);
    }
}
