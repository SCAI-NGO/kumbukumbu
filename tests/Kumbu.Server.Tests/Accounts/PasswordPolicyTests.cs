// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;

namespace Kumbu.Server.Tests.Accounts;

public sealed class PasswordPolicyTests
{
    [Fact]
    public void Eleven_characters_are_too_short() =>
        Assert.Equal("A password needs at least 12 characters.", PasswordPolicy.Check("Xk9#vT2!qLm"));

    [Fact]
    public void Twelve_characters_counted_as_characters_not_bytes_are_enough() =>
        Assert.Null(PasswordPolicy.Check("äöüßÄÖÜẞéèêë"));

    [Theory]
    [InlineData("unbelievable")]
    [InlineData("UnBelievable")]
    public void A_common_password_is_refused_whatever_its_case(string password) =>
        Assert.Equal("This password is one of the 10,000 most common ones.", PasswordPolicy.Check(password));

    [Fact]
    public void A_long_uncommon_password_is_fine() =>
        Assert.Null(PasswordPolicy.Check("correct horse battery staple"));
}
