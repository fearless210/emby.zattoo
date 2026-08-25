# Contributing

This project is an experimental Emby Live TV plugin for non-DRM Zattoo streams.

## Development checks

Use the .NET SDK selected by `global.json`, then run:

```bash
dotnet restore Emby.Zattoo.sln
dotnet build Emby.Zattoo.sln --configuration Release --no-restore
dotnet test Emby.Zattoo.sln --configuration Release --no-build --no-restore
dotnet format Emby.Zattoo.sln --verify-no-changes --no-restore
```

The Release build places the two manual plugin files in
`artifacts/Emby.Zattoo/`. Generated artifacts, build outputs and local tool
caches must not be committed.

## Security and test data

- Never commit or post a Zattoo username, password, cookie, token or signed
  playback URL.
- Never paste an unredacted Emby or FFmpeg log into an issue.
- Integration tests must remain opt-in and obtain credentials only from the
  process environment.
- Fixtures must use reserved or clearly invalid domains and synthetic secrets.
- DRM-protected streams remain unsupported; do not submit DRM bypass or
  decryption code.

## Scope

Keep the Zattoo Core independent from Emby-specific types. Changes to Emby Live
TV integration belong in `src/Emby.Zattoo.Plugin`; provider/session logic belongs
in `src/Emby.Zattoo.Core`.

## Contribution license

By submitting a contribution, you agree to license it under the Mozilla Public
License 2.0. Only submit work that you authored or that you have the right to
contribute under compatible terms. Do not copy source code from `pvr.zattoo` or
other third-party projects without an explicit license review and preservation
of all required notices.
