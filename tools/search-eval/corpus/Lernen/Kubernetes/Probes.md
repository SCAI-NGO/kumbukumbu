# Kubernetes probes

## Liveness

If the liveness probe fails, the kubelet restarts the container. A probe that checks a slow dependency causes restart loops under load.

## Readiness

A failing readiness probe only takes the pod out of the service's endpoints; nothing restarts.

## Startup

For slow starts: liveness and readiness wait until the startup probe has passed.
