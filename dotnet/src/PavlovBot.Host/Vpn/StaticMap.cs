using System.Globalization;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Vpn;

/// <summary>
/// A small map image for a coordinate, rendered by Geoapify.
/// </summary>
/// <remarks>
/// THE IMAGE IS FETCHED HERE AND UPLOADED AS AN ATTACHMENT, never linked. That is the
/// whole reason this class exists rather than a one-line <c>WithImageUrl(...)</c>.
///
/// Geoapify authenticates with the key in the QUERY STRING. Putting that URL in an embed
/// would publish the key to every person who can see the message - "copy image address" is
/// all it takes - and Discord stores the message payload indefinitely, so revoking the
/// staff channel later would not take it back. Ephemeral replies are no protection: the
/// recipient is exactly who would read it.
///
/// So the bot spends the bandwidth, and Discord only ever sees PNG bytes.
/// </remarks>
public sealed class StaticMap(HttpClient http, string apiKey, ILogger<StaticMap> logger)
{
    /// <summary>Comfortably inside every Discord upload limit, including no-boost servers.</summary>
    private const int MaxBytes = 2 * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// PNG bytes for a marked location, or null when no map could be produced.
    /// </summary>
    /// <remarks>
    /// Null on ANY failure, and the caller shows its embed without a map. A geolocation
    /// picture is the least important thing in an <c>/iplookup</c> - failing the whole
    /// command, or making an admin wait on a map service, to deliver it would be the wrong
    /// trade against the VPN verdict they actually asked for.
    /// </remarks>
    public async Task<byte[]?> RenderAsync(double latitude, double longitude, CancellationToken ct)
    {
        // Outside these a coordinate is not a place - it is a parsing accident, and
        // (0, 0) in particular is what several providers return for "unknown".
        if (double.IsNaN(latitude) || double.IsNaN(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180 ||
            (latitude == 0 && longitude == 0))
        {
            return null;
        }

        var lat = latitude.ToString("0.#####", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("0.#####", CultureInfo.InvariantCulture);

        var url =
            "https://maps.geoapify.com/v1/staticmap" +
            "?style=osm-bright-smooth&width=600&height=320&zoom=9" +
            $"&center=lonlat:{lon},{lat}" +
            $"&marker=lonlat:{lon},{lat};color:%23e04f5f;size:medium" +
            $"&apiKey={Uri.EscapeDataString(apiKey)}";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            using var response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The key is in the URL, so the URL never goes to the log.
                logger.LogWarning("Map render failed: {Status}", response.StatusCode);
                return null;
            }

            /* An error page served as 200 would otherwise be uploaded to Discord as a
               "map" and render as a broken image with no explanation anywhere. */
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Map render returned {MediaType}, not an image", mediaType);
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxBytes) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return bytes.Length is > 0 and <= MaxBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A map service being slow or down must not take the lookup with it.
            logger.LogWarning(ex, "Map render failed");
            return null;
        }
    }
}
