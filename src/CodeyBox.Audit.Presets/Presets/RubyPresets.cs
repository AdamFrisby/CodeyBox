namespace CodeyBox.Audit.Presets.Presets;

internal static class RubyPresets
{
    public static void Register(PresetCatalog catalog)
        => catalog.RegisterLanguage("ruby", _ =>
        [
            LanguagePresetHelpers.Shell(
                "ruby",
                "Gemfile/*.gemspec",
                LanguagePresetHelpers.RubyMarkerScript,
                "ruby:lint",
                "bundle", "exec", "rubocop"),
            LanguagePresetHelpers.Shell(
                "ruby",
                "Gemfile/*.gemspec",
                LanguagePresetHelpers.RubyMarkerScript,
                "ruby:test-pass",
                "bundle", "exec", "rspec"),
        ]);
}
