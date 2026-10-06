using System.Text;
using AgentForge.Api.Observability;
using FluentAssertions;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// The correlation id spans one hub invocation, so turns of one chat carry unrelated ids. This derives the key that joins them, from the session id that already
/// identifies the conversation - without putting that session id in a log line.
/// </summary>
public sealed class ConversationIdTests
{
    // Synthetic, like every fixture here. This is the shape of the `.AspNetCore.Session` COOKIE, not of
    // `ISession.Id`, which is a GUID string - `ChatHub.cs:58` passes the latter. The derivation hashes
    // whatever it is given, so the properties below hold for either; the length only fixes how many
    // positions the one-input-byte theory enumerates.
    private const string SessionId = "hqGmZ7nQ4VdKpL2rXtY8wBfE";

    [Fact]
    public void From_TheSameSessionId_ReturnsTheSameConversationId()
    {
        // The invariant the whole change exists for: turn 1 and turn 3 of one conversation must
        // join on this value. A derivation that varied per call would log a fresh id per turn and
        // reproduce exactly the gap being closed, while looking like it worked.
        ConversationId.From(SessionId).Should().Be(ConversationId.From(SessionId));
    }

    [Fact]
    public void From_DifferentSessionIds_ReturnDifferentConversationIds()
    {
        // Guards the opposite failure: a constant, or a derivation that collapses its input, would
        // merge unrelated clinicians' conversations into one apparent thread in the log store.
        ConversationId.From(SessionId).Should().NotBe(ConversationId.From("Zq3TmW9xPvR6sN1yUcA4dHgJ"));
    }

    [Fact]
    public void From_AnySessionId_DoesNotContainTheSessionIdVerbatim()
    {
        // Session.Id is the server-side session key. Emitting it - whole or in part - would put a
        // credential-shaped value in the same log index as everything else, and `no_phi_in_logs`
        // scores per-case PHI tokens, not secrets, so nothing else would catch it.
        var conversationId = ConversationId.From(SessionId);

        conversationId.Should().NotContain(SessionId);
        SessionId.Should().NotContain(conversationId);
    }

    [Fact]
    public void From_AnySessionId_IsNoContiguousSliceOfIt()
    {
        // `DoesNotContainTheSessionIdVerbatim` above compares strings, which a reversible derivation
        // passes:
        // hex of the session id's own leading bytes is not a substring of it in either direction,
        // yet decodes straight back to the server-side session key. So decode the value and assert
        // the bytes behind it are no window of the session id. This catches a derivation that
        // copies a slice; it does not catch one that copies a slice and transposes it, which is
        // what `DifferingAtOneCharacter` below is for.
        var decoded = Convert.FromHexString(ConversationId.From(SessionId));
        var sessionBytes = Encoding.UTF8.GetBytes(SessionId);

        var windows = Enumerable
            .Range(0, sessionBytes.Length - decoded.Length + 1)
            .Select(start => sessionBytes.AsSpan(start, decoded.Length).ToArray());

        windows.Should().NotContain(window => window.SequenceEqual(decoded));
    }

    public static TheoryData<int> EverySessionIdPosition()
    {
        var positions = new TheoryData<int>();
        for (var position = 0; position < SessionId.Length; position++)
        {
            positions.Add(position);
        }

        return positions;
    }

    [Theory]
    [MemberData(nameof(EverySessionIdPosition))]
    public void From_SessionIdsDifferingAtOneCharacter_StillDiffer(int position)
    {
        // One-wayness is what the <remarks> on ConversationId advertise, and
        // the checkable form of it is that the output depends on EVERY input byte. The slice checks
        // above are each blind to some reversible derivation: hex of the last eight bytes reversed
        // is no window of the input, is sixteen lowercase hex characters, and moves when the last
        // character moves - so it survives all of them and still decodes back to eight characters
        // of the server-side session key. It cannot survive this: changing byte 0 leaves it alone.
        // Held together with `IsBoundedLowercaseHex`, sixteen characters that depend on all
        // twenty-four force a compressing derivation, and a compressing one cannot be reversed.
        var replacement = SessionId[position] == 'a' ? 'b' : 'a';
        var mutated = string.Concat(SessionId[..position], replacement, SessionId[(position + 1)..]);

        mutated.Should().NotBe(SessionId).And.HaveLength(SessionId.Length);
        ConversationId.From(mutated).Should().NotBe(ConversationId.From(SessionId));
    }

    [Fact]
    public void From_SessionIdsSharingEveryByteButTheLast_StillDiffer()
    {
        // The derivation has to consume the whole session id. One reading a fixed-length prefix -
        // the leading eight bytes, say - hands two live sessions the same conversation id and
        // merges their turns into one apparent thread in the log store, which reads as a long
        // conversation rather than as a defect. `DifferentSessionIds` above cannot see that: its
        // two ids differ in the first character. Kept beside `DifferingAtOneCharacter`, which
        // generalises it, because prefix truncation is the specific mutant worth naming.
        const string sibling = "hqGmZ7nQ4VdKpL2rXtY8wBfF";

        sibling.Should().HaveLength(SessionId.Length).And.NotBe(SessionId);
        sibling[..^1].Should().Be(SessionId[..^1]);

        ConversationId.From(sibling).Should().NotBe(ConversationId.From(SessionId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void From_MissingSessionId_Throws(string sessionId)
    {
        // A blank session id means the caller has no conversation to key on. Returning a hash of
        // "" would silently join every such turn into one bogus conversation, which reads as data;
        // failing loudly is the boundary behaviour.
        var act = () => ConversationId.From(sessionId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void From_AnySessionId_IsBoundedLowercaseHex()
    {
        // The value becomes a log field on every chat turn, so it is a cardinality and parse
        // surface: a bounded hex token cannot widen a log line or carry a delimiter.
        var conversationId = ConversationId.From(SessionId);

        conversationId.Should().HaveLength(16);
        conversationId.Should().MatchRegex("^[0-9a-f]{16}$");
    }
}
