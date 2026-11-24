// SPDX-License-Identifier: AGPL-3.0-or-later
// The search overlay's relay (compose.search.yaml): the VM's HTTPS proxy listens on the host's loopback, which a
// bridge-networked container can't reach. On the host's network, this passes TCP from the Docker bridge's gateway
// address (reachable from the stack's containers only, never from outside the VM) to that proxy, byte for byte.
import { createConnection, createServer } from "node:net";

const [listenHost, listenPort, targetHost, targetPort] = [process.env.LISTEN_HOST, process.env.LISTEN_PORT, process.env.TARGET_HOST, process.env.TARGET_PORT];
if (!listenHost || !listenPort || !targetHost || !targetPort) throw new Error("LISTEN_HOST, LISTEN_PORT, TARGET_HOST and TARGET_PORT are needed");

createServer((client) => {
  const upstream = createConnection({ host: targetHost, port: Number(targetPort) });
  client.pipe(upstream).pipe(client);
  const close = () => {
    client.destroy();
    upstream.destroy();
  };
  client.on("error", close);
  upstream.on("error", close);
}).listen(Number(listenPort), listenHost, () => console.log(`relay ${listenHost}:${listenPort} -> ${targetHost}:${targetPort}`));
