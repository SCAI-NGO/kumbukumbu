# SPDX-License-Identifier: AGPL-3.0-or-later
"""The evaluation queries. Gold: (note path, heading of the section); '' is the text before the first heading,
and ('*', text) is every section containing text. A hit on any gold section counts."""

WP = "Technik/Projekte/Wärmepumpe Standort Nord.md"

QUERIES = [
    # L: exact terms, names, numbers (what rg would find too)
    ("L", "Durchfluss 1204", [(WP, "2026-04-22 Besprechung (Tobias)")]),
    ("L", "Entfernungspauschale", [("Privat/Finanzen/Steuer 2025.md", "Abzüge")]),
    ("L", "Tram 28", [("Privat/Reisen/Lissabon 2025.md", "Unterwegs")]),
    ("L", "ADR-007", [("Arbeit/Architektur/ADR-007 Message Queue.md", "ADR-007: Message Queue"), ("Arbeit/Architektur/ADR-007 Message Queue.md", "Entscheidung")]),
    ("L", "kubectl rollout restart", [("Demo/arbeit/logbuch.md", "2026-10-22")]),
    ("L", "Keycloak token lifetime", [("Arbeit/Architektur/ADR-009 Auth.md", "Decision")]),
    # P: paraphrase, same language
    ("P", "Was mache ich gegen die Schnecken im Beet?", [("Privat/Haus/Garten.md", "Schädlinge")]),
    ("P", "Wie lange vorher muss ich die Hausratversicherung kündigen?", [("Privat/Haus/Versicherungen.md", "Hausrat")]),
    ("P", "Wann muss das Auto zur Hauptuntersuchung?", [("Privat/Auto.md", "Termine")]),
    ("P", "Übungen gegen Rückenschmerzen", [("Privat/Gesundheit/Rücken.md", "Übungen")]),
    ("P", "Wer hat die Regelsprache des Gate-Projekts entworfen?", [("Demo/arbeit/personen/anna.md", "Anna")]),
    ("P", "Wie bekomme ich lange Belichtungszeiten bei hellem Tageslicht?", [("Lernen/Fotografie/Belichtung.md", "ND-Filter")]),
    ("P", "Was lief in der letzten Retro schlecht?", [("Arbeit/Meetings/Retro Sprint 42.md", "Was schlecht lief")]),
    ("P", "how wet should the bread dough be", [("Privat/Rezepte/Sourdough.md", "Dough")]),
    ("P", "why does my pod keep restarting", [("Lernen/Kubernetes/Probes.md", "Liveness")]),
    ("P", "what did we change after the outage", [("Arbeit/Incident 2026-09-03.md", "Action items")]),
    ("P", "fast intuitive versus slow deliberate thinking", [("Privat/Bücher.md", "Thinking, Fast and Slow")]),
    # C: across languages
    ("C", "Warum ist die Datenbank im September ausgefallen?", [("Arbeit/Incident 2026-09-03.md", "Root cause")]),
    ("C", "Bei welcher Temperatur backe ich das Brot?", [("Privat/Rezepte/Sourdough.md", "Baking")]),
    ("C", "why did we pick RabbitMQ over Kafka", [("Arbeit/Architektur/ADR-007 Message Queue.md", "Begründung")]),
    ("C", "when is the heat pump supposed to start operating", [(WP, "2026-03-12 Entscheidung Wärmepumpe")]),
    ("C", "who looks after the new colleague in her first weeks", [("Arbeit/Onboarding Lea.md", "Erste Woche")]),
    ("C", "spices for the lentil soup", [("Privat/Rezepte/Linsensuppe.md", "Zutaten")]),
    ("C", "Wie funktioniert der Borrow Checker?", [("Lernen/Rust/Ownership.md", "Borrowing")]),
    ("C", "customer complaint about the invoice export", [("Arbeit/Kunden/Stadtwerke.md", "Offene Punkte")]),
    # W: needs world knowledge (the note says it differently)
    ("W", "Warum heizen wir am Standort Nord nicht mit Erdwärme?", [(WP, "2026-03-12 Entscheidung Wärmepumpe")]),
    ("W", "Wo gibt es in Lissabon die berühmten Puddingtörtchen?", [("Privat/Reisen/Lissabon 2025.md", "Essen")]),
    ("W", "Was bringt mir das Pendeln bei der Steuer?", [("Privat/Finanzen/Steuer 2025.md", "Abzüge")]),
    ("W", "Sind wir gegen Hochwasser versichert?", [("Privat/Haus/Versicherungen.md", "Elementarschaden")]),
    ("W", "Göttin mit drei Gesichtern", [("Demo/arbeit/ideen/namensideen.md", "Namensideen für Projekte")]),
    ("W", "Ist das mit dem Lärm an der Grundstücksgrenze geklärt?", [("*", "Schallmessung")]),
    ("W", "Was hält den Sauerteig am Leben, wenn ich verreise?", [("Privat/Rezepte/Sourdough.md", "Starter")]),
    # V: vague, or a distractor nearby
    ("V", "Was steht bei der Heizung zu Hause als Nächstes an?", [("Privat/Haus/Heizung.md", "Nächste Schritte"), ("Privat/Haus/Heizung.md", "Angebot")]),
    ("V", "cloud costs next year", [("Arbeit/Budget 2027.md", "Cloud")]),
    ("V", "Der Akkord, der auf der Gitarre noch nicht klappt", [("Privat/Musik/Gitarre.md", "Barré-Griffe"), ("Lernen/Musik/Gitarre.md", "Barré-Griffe")]),
    ("V", "Förderung für die Wärmepumpe", [("Privat/Haus/Heizung.md", "Angebot"), ("*", "Förderantrag BEG")]),
    ("V", "Zertifikate laufen ab", [("Demo/arbeit/meetings/2026-10-14.md", "Jour fixe 2026-10-14"), ("Demo/arbeit/todo.md", "Todo")]),
    # N: nothing in the notes answers these
    ("N", "Wie hoch ist der Mount Everest?", []),
    ("N", "Rezept für Tiramisu", []),
    ("N", "Kubernetes autoscaling configuration", []),
]
