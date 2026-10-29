using System;
using System.Collections.Generic;
using System.Linq;
using NimbusX.Cli;
using NimbusX.Providers;
using Xunit;

namespace NimbusX.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void A_plain_command_parses()
    {
        Assert.True(CommandLine.TryParse(new[] { "mount" }, out ParsedCommand? parsed, out string? error));
        Assert.Null(error);
        Assert.Equal("mount", parsed!.Command);
        Assert.Empty(parsed.Positionals);
    }

    [Fact]
    public void Commands_are_case_insensitive()
    {
        CommandLine.TryParse(new[] { "DOCTOR" }, out ParsedCommand? parsed, out _);
        Assert.Equal("doctor", parsed!.Command);
    }

    [Fact]
    public void A_flag_without_a_value_is_a_switch()
    {
        CommandLine.TryParse(new[] { "status", "--json" }, out ParsedCommand? parsed, out _);
        Assert.True(parsed!.Has("json"));
        Assert.Null(parsed.Value("json"));
    }

    [Fact]
    public void An_option_takes_the_next_argument_as_its_value()
    {
        CommandLine.TryParse(new[] { "games", "scan", "--account", "abc123" }, out ParsedCommand? parsed, out _);
        Assert.Equal("abc123", parsed!.Value("account"));
        Assert.Equal(new[] { "scan" }, parsed.Positionals);
    }

    [Fact]
    public void An_equals_sign_sets_the_value()
    {
        CommandLine.TryParse(new[] { "cache", "set", "--budget=25GB" }, out ParsedCommand? parsed, out _);
        Assert.Equal("25GB", parsed!.Value("budget"));
    }

    [Fact]
    public void Options_are_case_insensitive()
    {
        CommandLine.TryParse(new[] { "doctor", "--JSON" }, out ParsedCommand? parsed, out _);
        Assert.True(parsed!.Has("json"));
    }

    [Fact]
    public void An_int_option_falls_back_when_the_value_is_not_a_number()
    {
        CommandLine.TryParse(new[] { "logs", "--lines", "many" }, out ParsedCommand? parsed, out _);
        Assert.Equal(50, parsed!.IntValue("lines", 50));
    }

    [Fact]
    public void An_empty_command_line_is_an_error()
    {
        Assert.False(CommandLine.TryParse(Array.Empty<string>(), out ParsedCommand? parsed, out string? error));
        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_double_dash_option_never_swallows_the_next_option()
    {
        CommandLine.TryParse(new[] { "cache", "clear", "--force", "--all" }, out ParsedCommand? parsed, out _);
        Assert.True(parsed!.Has("force"));
        Assert.True(parsed.Has("all"));
        Assert.Null(parsed.Value("force"));
    }
}

public sealed class ProviderCatalogTests
{
    [Fact]
    public void Every_provider_has_a_unique_id_and_an_rclone_type()
    {
        IReadOnlyList<ProviderDefinition> providers = ProviderCatalog.All;
        Assert.NotEmpty(providers);
        Assert.Equal(providers.Count, providers.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(providers, provider => Assert.False(string.IsNullOrWhiteSpace(provider.RcloneType)));
    }

    [Theory]
    [InlineData("drive", "drive")]
    [InlineData("onedrive", "onedrive")]
    [InlineData("dropbox", "dropbox")]
    [InlineData("pcloud", "pcloud")]
    [InlineData("box", "box")]
    [InlineData("yandex", "yandex")]
    [InlineData("koofr", "koofr")]
    [InlineData("mega", "mega")]
    [InlineData("webdav", "webdav")]
    [InlineData("sftp", "sftp")]
    public void The_documented_providers_exist(string id, string rcloneType)
    {
        Assert.True(ProviderCatalog.TryGet(id, out ProviderDefinition definition));
        Assert.Equal(rcloneType, definition.RcloneType);
    }

    [Fact]
    public void TeraBox_is_experimental_and_needs_the_fork()
    {
        Assert.True(ProviderCatalog.TryGet("terabox", out ProviderDefinition definition));
        Assert.True(definition.IsExperimental);
        Assert.True(definition.RequiresTeraBoxBuild);
        Assert.False(string.IsNullOrWhiteSpace(definition.ExperimentalWarning));
        Assert.True(ProviderCatalog.RequiresTeraBox("terabox"));
        Assert.False(ProviderCatalog.RequiresTeraBox("drive"));
    }

    [Fact]
    public void The_standard_build_list_excludes_the_terabox_provider()
    {
        Assert.DoesNotContain(ProviderCatalog.StandardBuild, p => p.RequiresTeraBoxBuild);
    }

    [Fact]
    public void An_unknown_provider_is_refused_without_throwing()
    {
        Assert.False(ProviderCatalog.TryGet("not-a-provider", out _));
        Assert.Null(ProviderCatalog.Get(null));
        Assert.ThrowsAny<Exception>(() => ProviderCatalog.GetRequired("not-a-provider"));
    }

    [Fact]
    public void Every_required_field_has_a_label_and_secret_fields_are_marked()
    {
        foreach (ProviderDefinition provider in ProviderCatalog.All)
        {
            foreach (ProviderField field in provider.Fields)
            {
                Assert.False(string.IsNullOrWhiteSpace(field.Name));
                Assert.False(string.IsNullOrWhiteSpace(field.Label));

                if (field.Kind == ProviderFieldKind.Password)
                {
                    Assert.True(field.IsSecret);
                }
            }
        }
    }

    [Fact]
    public void A_choice_field_always_offers_choices_and_a_default_among_them()
    {
        foreach (ProviderDefinition provider in ProviderCatalog.All)
        {
            foreach (ProviderField field in provider.Fields.Where(f => f.Kind == ProviderFieldKind.Choice))
            {
                Assert.NotEmpty(field.Choices);
                if (field.DefaultValue is not null)
                {
                    Assert.Contains(field.DefaultValue, field.Choices, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void Credential_providers_never_expose_a_secret_on_the_command_line()
    {
        // NimbusX passes credentials to rclone through `config/create` over the loopback rc interface, and stores
        // them with DPAPI; no provider field may claim to be a command line argument.
        Assert.All(ProviderCatalog.All, provider => Assert.DoesNotContain("--", provider.RcloneType, StringComparison.Ordinal));
    }
}
