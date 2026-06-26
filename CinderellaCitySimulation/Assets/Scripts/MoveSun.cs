
using UnityEngine;

/// <summary>
/// Drives a child "Sun" directional light along an approximate real-world solar
/// path using a single Time of Day slider. The geographic location is hard-coded
/// to Englewood, Colorado, and the path is an approximation intended for art
/// direction rather than astronomical accuracy.
///
/// Attach this to the Environment GameObject (the parent of the Sun). With an
/// HDRP physically based sky, rotating the directional light's transform also
/// moves the rendered sun disc, so adjusting the sliders moves both together.
/// </summary>

// attach to the Environment GameObject; expects a directional Light somewhere in its children
[ExecuteAlways]
public class MoveSun : MonoBehaviour
{
    [Header("Sun")]
    [Tooltip("The Sun (directional light) to drive. If left empty, the first Light found in this object's children is used.")]
    public Transform sun;

    [Header("Time")]
    [Tooltip("Hour of the day in 24h solar time. 12 = solar noon (sun at its highest point).")]
    [Range(0f, 24f)]
    public float timeOfDay = 12f;

    [Tooltip("Day of the year (1 = Jan 1 ... 365 = Dec 31). Controls how high the sun climbs across the seasons.")]
    [Range(1, 365)]
    public int dayOfYear = 172; // approximately the summer solstice

    [Header("Location (Englewood, Colorado)")]
    [Tooltip("Latitude in degrees, north positive. Hard-coded approximation for Englewood, CO.")]
    public float latitude = 39.6478f;

    [Tooltip("Longitude in degrees, east positive. Kept for reference; the slider is treated as local solar time.")]
    public float longitude = -104.9878f;

    [Tooltip("Mirror the sun's east/west travel. Needed because Unity's left-handed coordinate system flips a real-world compass bearing onto the X axis. Leave on for a scene whose +Z points north.")]
    public bool flipEastWest = true;

    // last computed values, surfaced for the custom inspector readout
    [HideInInspector] public float currentElevation;
    [HideInInspector] public float currentAzimuth;

    private void OnEnable()
    {
        UpdateSunPosition();
    }

    // called by the editor whenever a slider changes, so the sun tracks live in edit mode
    private void OnValidate()
    {
        UpdateSunPosition();
    }

    private void Update()
    {
        // keep tracking at runtime in case the slider is animated or driven by other systems
        if (Application.isPlaying)
        {
            UpdateSunPosition();
        }
    }

    /// <summary>
    /// Recomputes the sun's orientation from the current time, day, and latitude
    /// and applies it to the sun transform.
    /// </summary>
    public void UpdateSunPosition()
    {
        Transform target = ResolveSun();
        if (target == null)
        {
            return;
        }

        // solar declination (degrees): the seasonal north/south tilt of the sun
        float declination = 23.45f * Mathf.Sin(Mathf.Deg2Rad * (360f * (284 + dayOfYear) / 365f));

        // hour angle (degrees): 0 at solar noon, negative in the morning, positive in the afternoon
        float hourAngle = 15f * (timeOfDay - 12f);

        float latRad = Mathf.Deg2Rad * latitude;
        float declRad = Mathf.Deg2Rad * declination;
        float hourRad = Mathf.Deg2Rad * hourAngle;

        // elevation: angle of the sun above the horizon
        float sinElevation = Mathf.Sin(latRad) * Mathf.Sin(declRad)
                             + Mathf.Cos(latRad) * Mathf.Cos(declRad) * Mathf.Cos(hourRad);
        sinElevation = Mathf.Clamp(sinElevation, -1f, 1f);
        float elevation = Mathf.Asin(sinElevation); // radians

        // azimuth: compass direction of the sun, measured clockwise from north
        float cosAzimuth = (Mathf.Sin(declRad) - Mathf.Sin(elevation) * Mathf.Sin(latRad))
                           / (Mathf.Cos(elevation) * Mathf.Cos(latRad));
        cosAzimuth = Mathf.Clamp(cosAzimuth, -1f, 1f);
        float azimuth = Mathf.Acos(cosAzimuth); // radians, measured from north (0..PI)

        // before solar noon the sun is in the east; after, mirror it into the west
        if (hourAngle > 0f)
        {
            azimuth = (2f * Mathf.PI) - azimuth;
        }

        currentElevation = elevation * Mathf.Rad2Deg;
        currentAzimuth = azimuth * Mathf.Rad2Deg;

        // direction FROM the ground TO the sun, in Unity world space (+Y up, +Z north).
        // east/west maps onto X; it is mirrored by default to correct for Unity's left-handed axes.
        float eastWestSign = flipEastWest ? -1f : 1f;
        Vector3 directionToSun = new Vector3(
            eastWestSign * Mathf.Cos(elevation) * Mathf.Sin(azimuth),
            Mathf.Sin(elevation),
            Mathf.Cos(elevation) * Mathf.Cos(azimuth));

        // a directional light emits along its forward axis (from the sun down toward the scene)
        if (directionToSun.sqrMagnitude > 0.0001f)
        {
            target.rotation = Quaternion.LookRotation(-directionToSun, Vector3.up);
        }
    }

    // returns the assigned sun, or finds and caches the first child Light if none is set
    private Transform ResolveSun()
    {
        if (sun != null)
        {
            return sun;
        }

        Light childLight = GetComponentInChildren<Light>();
        if (childLight != null)
        {
            sun = childLight.transform;
            return sun;
        }

        return null;
    }
}
