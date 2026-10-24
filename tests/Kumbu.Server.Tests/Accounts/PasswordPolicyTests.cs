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

    [Theory]
    [InlineData("password1234")]
    [InlineData("Qwertyuiop!!")]
    [InlineData("!!dragon2024!!")]
    [InlineData("  Pass\u0077ord 2024 ")]
    public void A_common_password_with_digits_or_punctuation_around_it_is_refused(string password) =>
        Assert.Equal("This password is a common one with digits or punctuation around it.", PasswordPolicy.Check(password));

    [Fact]
    public void A_common_word_as_part_of_a_passphrase_is_fine() =>
        Assert.Null(PasswordPolicy.Check("2024 dragon slayer of the north!"));

    [Fact]
    public void A_long_uncommon_password_is_fine() =>
        Assert.Null(PasswordPolicy.Check("correct horse battery staple"));
}
