// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Dav;

/// <summary>
/// The room for parked bodies being read (WebDAV design §3.8): <see cref="DavRules.ParkingReadBudget"/> bytes in all,
/// <see cref="DavRules.ParkingReadPerUser"/> for the shares of each user. A body is read into memory whole before
/// <see cref="DavParking"/> keeps or evicts anything, so its bytes are taken from here before it is read and given back
/// once it is parked or refused; all at once, so no request holds room while it waits for more. A request that finds no
/// room waits for it, in the order they came, rather than being refused: junk is small and quick, and a refusal would
/// abort a Finder copy. A user held back by her own part lets other users past, so her slow uploads hold back her
/// alone; her later requests wait behind the one held back, so a large one isn't overtaken for ever.
/// </summary>
public sealed class DavParkingBudget
{
    private readonly Lock _gate = new();
    private readonly LinkedList<Waiter> _waiting = [];
    private readonly Dictionary<string, long> _byUser = new(StringComparer.Ordinal);
    private long _taken;

    /// <summary><paramref name="bytes"/> (at most <see cref="DavRules.ParkingReadPerUser"/>) of the budget for a share
    /// of <paramref name="userId"/>, once there is room; given back when the result is disposed. Cancelled with
    /// <paramref name="ct"/> while it waits. An empty body takes nothing, and never waits.</summary>
    public async Task<IDisposable> TakeAsync(string userId, long bytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, DavRules.ParkingReadPerUser);
        if (bytes == 0) return Nothing.Instance;
        var waiter = new Waiter(userId, bytes);
        lock (_gate)
        {
            _waiting.AddLast(waiter.Node);
            Grant();
        }

        if (!waiter.Granted.Task.IsCompleted)
        {
            await using (ct.Register(() => Cancel(waiter)))
            {
                await waiter.Granted.Task;
            }
        }

        return new Room(this, waiter);
    }

    private void Cancel(Waiter waiter)
    {
        lock (_gate)
        {
            // Granted meanwhile: the caller has its room and gives it back.
            if (waiter.Node.List is null) return;
            _waiting.Remove(waiter.Node);
            waiter.Granted.TrySetCanceled();
            // One that held the others back may have gone.
            Grant();
        }
    }

    private void Release(Waiter waiter)
    {
        lock (_gate)
        {
            _taken -= waiter.Bytes;
            if ((_byUser[waiter.UserId] -= waiter.Bytes) == 0) _byUser.Remove(waiter.UserId);
            Grant();
        }
    }

    /// <summary>Lets in the waiting requests there is room for, in order. A user held back by her own part is passed,
    /// with all her later requests; the first request held back by the budget stops the others.</summary>
    private void Grant()
    {
        HashSet<string>? passed = null;
        for (var node = _waiting.First; node is not null;)
        {
            var next = node.Next;
            var waiter = node.Value;
            var user = _byUser.GetValueOrDefault(waiter.UserId);
            if (passed?.Contains(waiter.UserId) != true)
            {
                if (user + waiter.Bytes > DavRules.ParkingReadPerUser)
                {
                    (passed ??= new(StringComparer.Ordinal)).Add(waiter.UserId);
                }
                else
                {
                    if (_taken + waiter.Bytes > DavRules.ParkingReadBudget) return;
                    _taken += waiter.Bytes;
                    _byUser[waiter.UserId] = user + waiter.Bytes;
                    _waiting.Remove(node);
                    waiter.Granted.TrySetResult();
                }
            }

            node = next;
        }
    }

    private sealed class Waiter
    {
        public Waiter(string userId, long bytes)
        {
            (UserId, Bytes) = (userId, bytes);
            Node = new LinkedListNode<Waiter>(this);
        }

        public string UserId { get; }

        public long Bytes { get; }

        public LinkedListNode<Waiter> Node { get; }

        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The room of an empty body: none.</summary>
    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>The bytes taken, given back once.</summary>
    private sealed class Room(DavParkingBudget budget, Waiter waiter) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) budget.Release(waiter);
        }
    }
}
