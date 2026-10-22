// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Tests;

internal static class TestImages
{
    // MongoDB 8.x exits on startup on Linux kernels 6.19–7.0.13 (SERVER-121912 / SERVER-125742),
    // which includes current GitHub runners. 7.0 runs on every kernel and covers what the tests use.
    public const string Mongo = "mongo:7.0";
}
