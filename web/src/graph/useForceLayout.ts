// SPDX-License-Identifier: AGPL-3.0-or-later
import { type Simulation, type SimulationLinkDatum, type SimulationNodeDatum, forceCollide, forceLink, forceManyBody, forceSimulation } from "d3-force";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { type Placed, seedPositions } from "./layout";
import type { GEdge, GNode, SizeBy } from "./model";
import { collideRadius, linkDistance, linkStrength, radius } from "./scales";
import { similarity } from "./similarity";

type SimNode = SimulationNodeDatum & { id: string; key: string; r: number; centre: boolean };
type SimLink = SimulationLinkDatum<SimNode> & { s: number };
type Positions = ReadonlyMap<string, { x: number; y: number }>;
type Opts = { centreKey: string; sizeBy: SizeBy; reducedMotion: boolean; running: boolean };

const CHARGE = -120;
const REHEAT = 0.3;

/**
 * The graph's layout, by node id. The simulation lives in a ref and ticks on animation frames, never in React state;
 * positions are published at most once a frame. A new answer keeps the places of the nodes it shares with the last
 * one and warms the simulation gently instead of restarting it. An answer is new only when its nodes, radii or edges
 * differ from the last one's; new arrays of the same graph leave the layout alone.
 */
export function useForceLayout(nodes: GNode[], edges: GEdge[], opts: Opts): { positions: Positions; drag(id: string, x: number | null, y: number | null): void } {
  const { centreKey, sizeBy, reducedMotion, running } = opts;
  const sim = useRef<Simulation<SimNode, SimLink> | null>(null);
  const frame = useRef(0);
  const signature = useRef("");
  const live = useRef(opts);
  live.current = opts;
  const [positions, setPositions] = useState<Positions>(() => new Map());

  const publish = useCallback(() => {
    const s = sim.current;
    if (s) setPositions(new Map(s.nodes().map((n) => [n.id, { x: n.x!, y: n.y! }])));
  }, []);

  const step = useCallback(() => {
    frame.current = 0;
    const s = sim.current;
    if (!s) return;
    s.tick();
    publish();
    if (s.alpha() >= s.alphaMin() || s.alphaTarget() >= s.alphaMin()) frame.current = requestAnimationFrame(step);
  }, [publish]);

  // Runs the simulation on: with reduced motion straight to rest, otherwise frame by frame.
  const go = useCallback(() => {
    const s = sim.current;
    if (!s || !live.current.running || frame.current) return;
    if (live.current.reducedMotion) {
      // A node held from before the switch stays where it is pinned; only a target of 0 lets the loop end.
      s.alphaTarget(0);
      while (s.alpha() >= s.alphaMin()) s.tick();
      publish();
    } else if (s.alpha() >= s.alphaMin()) frame.current = requestAnimationFrame(step);
  }, [publish, step]);

  const halt = () => {
    cancelAnimationFrame(frame.current);
    frame.current = 0;
  };

  // Before the paint, so that with reduced motion the first thing shown is the layout at rest.
  useLayoutEffect(() => {
    // A hint that changes only a time or a size brings new arrays of the same graph: the layout stays, with no reheat.
    const keyOf = new Map(nodes.map((n) => [n.id, n.key]));
    // Only edges with both ends in the answer: forceLink throws on an id it cannot find.
    const kept = edges.filter((e) => keyOf.has(e.source) && keyOf.has(e.target));
    const shape = JSON.stringify([centreKey, nodes.map((n) => [n.id, n.key, radius(n, sizeBy)]), kept.map((e) => [e.source, e.target])]);
    if (sim.current && shape === signature.current) return;
    signature.current = shape;
    halt();
    // The last layout, shifted so the new centre's old place is the origin: everything keeps its place around it.
    const old = sim.current?.nodes() ?? [];
    const origin = old.find((n) => n.key === centreKey) ?? { x: 0, y: 0 };
    const previous = new Map<string, Placed>();
    for (const n of old) if (!previous.has(n.key)) previous.set(n.key, { key: n.key, x: n.x! - origin.x!, y: n.y! - origin.y! });

    const seeds = seedPositions(nodes, edges, previous, centreKey);
    const simNodes = nodes.map((n): SimNode => {
      const p = seeds.get(n.id)!;
      return { id: n.id, key: n.key, r: collideRadius(radius(n, sizeBy)), centre: p.fixed, x: p.x, y: p.y, ...(p.fixed ? { fx: 0, fy: 0 } : {}) };
    });
    const links = kept.map((e): SimLink => ({ source: e.source, target: e.target, s: similarity(keyOf.get(e.source)!, keyOf.get(e.target)!).score }));
    const forces = (s: Simulation<SimNode, SimLink>) =>
      s
        .force("link", forceLink<SimNode, SimLink>(links).id((n) => n.id).distance((l) => linkDistance(l.s)).strength((l) => linkStrength(l.s)))
        .force("charge", forceManyBody<SimNode>().strength(CHARGE))
        .force("collide", forceCollide<SimNode>((n) => n.r));

    // A new answer lets go of any drag: its nodes are new and unpinned.
    if (sim.current) forces(sim.current.nodes(simNodes)).alphaTarget(0).alpha(REHEAT);
    else sim.current = forces(forceSimulation(simNodes).stop());
    publish();
    go();
  }, [nodes, edges, centreKey, sizeBy, publish, go]);

  useEffect(() => {
    halt();
    go();
  }, [running, reducedMotion, go]);

  useEffect(
    () => () => {
      halt();
      sim.current?.stop();
    },
    [],
  );

  // Pins a node where it is dragged while held; null lets it go. The centre stays pinned in the middle.
  const drag = useCallback(
    (id: string, x: number | null, y: number | null) => {
      const s = sim.current;
      if (!s) return;
      // A release always ends the drag's warmth, even for a node a new answer has dropped.
      if (x === null) s.alphaTarget(0);
      const n = s.nodes().find((m) => m.id === id);
      if (!n || n.centre) return;
      n.fx = x;
      n.fy = y;
      if (live.current.reducedMotion) {
        if (x !== null) [n.x, n.y] = [x, y!];
        s.alpha(REHEAT);
        go();
      } else {
        if (x !== null) s.alphaTarget(REHEAT).alpha(Math.max(s.alpha(), REHEAT));
        go();
      }
    },
    [go],
  );

  return { positions, drag };
}
