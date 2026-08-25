using System;
using System.Threading;
using Emby.Zattoo.Plugin.Configuration;
using MediaBrowser.Common;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Logging;

namespace Emby.Zattoo.Plugin
{
    /// <summary>Emby server plugin entry point.</summary>
    public sealed class Plugin : BasePluginSimpleUI<ZattooPluginOptions>
    {
        private static readonly Guid PluginId = new Guid(
            "d90bb3a8-daa2-4a5e-bf0b-56962e8b80e2");

        private readonly ILogger logger;
        private readonly ZattooPasswordStore passwordStore;
        private long configurationRevision;

        public Plugin(
            IApplicationHost applicationHost,
            IEncryptionManager encryptionManager,
            ILogManager logManager)
            : base(applicationHost)
        {
            Instance = this;
            passwordStore = new ZattooPasswordStore(encryptionManager);
            logger = logManager.GetLogger(Name);
            logger.Info("Emby.Zattoo plugin loaded; DRM streams remain unsupported.");
        }

        internal static Plugin? Instance { get; private set; }

        public override string Name => "Zattoo Live TV";

        public override string Description =>
            "Live TV tuner for non-DRM Zattoo channels with server-side remux.";

        public override Guid Id => PluginId;

        internal ZattooRuntimeSettings GetRuntimeSettings(out long revision)
        {
            var options = GetOptions();
            var password = passwordStore.Unprotect(options.Password);
            revision = Interlocked.Read(ref configurationRevision);
            return ZattooRuntimeSettings.FromConfiguration(options, password);
        }

        protected override ZattooPluginOptions OnBeforeShowUI(
            ZattooPluginOptions options)
        {
            return new ZattooPluginOptions
            {
                Username = options.Username,
                Password = passwordStore.GetDisplayValue(options.Password),
                PreferredQuality = options.PreferredQuality,
                FfmpegPath = options.FfmpegPath,
                ProviderUrl = options.ProviderUrl,
                ApplicationVersion = options.ApplicationVersion,
            };
        }

        protected override bool OnOptionsSaving(ZattooPluginOptions options)
        {
            options.Username = options.Username?.Trim() ?? string.Empty;
            options.FfmpegPath = options.FfmpegPath?.Trim() ?? string.Empty;
            options.ProviderUrl = options.ProviderUrl?.Trim() ?? string.Empty;
            options.ApplicationVersion = options.ApplicationVersion?.Trim()
                ?? string.Empty;
            options.ValidateOrThrow();

            var current = GetOptions();
            options.Password = passwordStore.ProtectSubmittedValue(
                options.Password,
                current.Password);
            return base.OnOptionsSaving(options);
        }

        protected override void OnOptionsSaved(ZattooPluginOptions options)
        {
            Interlocked.Increment(ref configurationRevision);
            logger.Info("Zattoo plugin configuration updated.");
            base.OnOptionsSaved(options);
        }
    }
}
