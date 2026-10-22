// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Fakes;

public static class TestShell
{
    public static Shell Of(params (string Path, string Content)[] notes) => Of(new InMemoryNoteStore(notes));

    public static Shell Of(InMemoryNoteStore store, TimeProvider? time = null) =>
        new(store, new NoteWriter(store, time ?? new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1))));
}
