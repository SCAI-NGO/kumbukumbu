// SPDX-License-Identifier: AGPL-3.0-or-later
import { newerOnServer, parseVersion } from "./version";

test("parses a release and a pre-release with their commit", () => {
  expect(parseVersion("0.2.0+a6362ec")).toEqual({ semver: "0.2.0", sha: "a6362ec" });
  expect(parseVersion("0.1.0-pre.42+abc1234")).toEqual({ semver: "0.1.0-pre.42", sha: "abc1234" });
});

test("development builds and versions without a commit switch detection off", () => {
  for (const v of ["0.0.0-dev", "0.0.0-dev+abc1234", "0.1.0", "", null, undefined]) expect(parseVersion(v)).toBeNull();
  expect(newerOnServer("0.0.0-dev", "0.2.0+a6362ec")).toBeNull();
  expect(newerOnServer("0.2.0+a6362ec", "0.1.0")).toBeNull();
});

test("a different server build is an update, the same one is not", () => {
  expect(newerOnServer("0.1.0-pre.41+aaaaaaa", "0.2.0+a6362ec")).toEqual({ semver: "0.2.0", sha: "a6362ec" });
  expect(newerOnServer("0.2.0+a6362ec", "0.2.0+a6362ec")).toBeNull();
});
