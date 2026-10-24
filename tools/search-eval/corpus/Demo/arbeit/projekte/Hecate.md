---
status: aktiv
tags: [projekt, gateway]
---
# Hecate

Hecate ist unser Gate-Service: Er entscheidet, welche Anfragen in welches Backend dürfen.
Benannt nach der griechischen Göttin der Wegkreuzungen.

## Architektur

- Eingang: `hecate-proxy` (Envoy-basiert)
- Regeln: YAML in `hecate-rules/`
- Team: [[anna]], [[ben]]

## Zeitplan

- Hecate 2.0 geht im November live.
- Rückblick auf Version 1: [[Hecate Retrospektive]]
