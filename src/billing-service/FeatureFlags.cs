// SPDX-FileCopyrightText: 2026 Cedric Moulard / Kraftr
// SPDX-License-Identifier: MIT

using OpenFeature;
using OpenFeature.Contrib.Providers.Flagd;

namespace BillingService;

public static class FeatureFlags
{
    public static async Task InitAsync()
    {
        var host = Environment.GetEnvironmentVariable("FLAGD_HOST") ?? "flagd";
        var portStr = Environment.GetEnvironmentVariable("FLAGD_PORT") ?? "8013";
        var port = int.Parse(portStr);

        var provider = new FlagdProvider(new Uri($"http://{host}:{port}"));
        await Api.Instance.SetProviderAsync(provider);
    }

    public static FeatureClient Client => Api.Instance.GetClient();
}
