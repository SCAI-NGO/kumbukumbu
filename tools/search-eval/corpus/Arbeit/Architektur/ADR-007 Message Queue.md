# ADR-007: Message Queue

## Kontext

Die Gate-Dienste brauchen asynchrone Benachrichtigungen, etwa 200 Nachrichten pro Sekunde in der Spitze.

## Entscheidung

Wir nehmen RabbitMQ, nicht Kafka.

## Begründung

Das Team betreibt RabbitMQ seit Jahren, das Volumen ist klein, und wir brauchen keine Wiedergabe alter Nachrichten. Kafka wäre für diese Last überdimensioniert und im Betrieb aufwendiger.

## Folgen

Kein Event-Sourcing über die Queue. Sollte das nötig werden, neu entscheiden.
