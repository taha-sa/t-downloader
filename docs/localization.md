# Localization

The single source of UI text is `src/TDownloader.Core/Localization/Strings.resx`
(neutral English), `Strings.tr.resx`, and `Strings.de.resx`. `Strings.cs` provides
strongly typed properties and formatting methods. Add new keys and accessors
together; format values with the selected culture. Keep application names intact.
Follow the T-Suite TR/EN/DE glossary: Turkish uses “siz”, German uses “Sie”,
sentence case applies, and errors describe the problem and the next action.

Settings stores `Language` as `""` (system), `tr`, `en`, or `de`. Startup applies
both cultures before creating windows, including the second-instance notice.
System mode preserves the Windows user's UI language and regional format settings.
Unsupported UI languages fall back to neutral English resources. Changing the
preference offers a restart; declining keeps the current UI until the next launch.
Download category identifiers and folder mappings stay stable across languages.

The SDK has no localization API for its sealed WinForms dialogs. App-local copies
in `src/TDownloader.App/Sdk` use the shared resources and existing SDK controllers
and crash-report services. The sibling SDK source is unchanged. When upgrading
the SDK, review these two presentation adapters against their upstream files.
Diagnostic payloads, protocol keys, paths, product names, and user content are
preserved rather than translated.

T-Bridge actions in the completed window always remain visible. An absent bridge
opens a fresh, contextual invitation with its official icon and release link.
Closing the invitation affects only that instance. If T-Bridge is installed but
no phone is connected, the actions are disabled with a localized instruction.
Installation detection checks the T-Suite registry and known installation paths;
the existing shared poller refreshes connection and installation status.

The browser extension consumes generated `Ext*` resource entries. After editing
them run `powershell -File tools/Export-ExtensionStrings.ps1`. The desktop sends
its active UI culture through the optional `settings.language` field; before it
connects, the extension uses the browser language. Generated manifest descriptions
use Chromium's standard locale catalogs. CI checks generated files with `-Check`
and runs the extension tests.

`LocalizationTests.Resources_have_identical_keys_and_format_arguments` runs in the
existing Host xUnit project and CI. It checks identical key sets, nonempty
translations, valid format placeholders, and punctuation. Additional tests cover
all three languages, English fallback, regional number/date/unit formatting, and
language persistence. Run:

```powershell
dotnet test -v q
dotnet build TDownloader.sln -c Release -v q
powershell -File tools/Export-ExtensionStrings.ps1 -Check
node extension/tests/run.mjs
dotnet build-server shutdown
```

The HTTP listener and Windows DPAPI integration tests require an unrestricted
Windows user session with its profile loaded; restricted runners may reject those
operations before the download code runs.
