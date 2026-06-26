using UnityEditor;
using UnityEngine;

/// <summary>
/// Custom inspector for MoveSun. Draws the default sliders and adds a
/// human-readable readout (clock time, elevation, azimuth) so the Time of Day
/// slider is easy to reason about while dragging.
/// </summary>
[CustomEditor(typeof(MoveSun))]
public class MoveSunEditor : Editor
{
    public override void OnInspectorGUI()
    {
        MoveSun positioner = (MoveSun)target;

        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        if (EditorGUI.EndChangeCheck())
        {
            // refresh the sun immediately as sliders are dragged in edit mode
            positioner.UpdateSunPosition();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Readout", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Time", FormatTime(positioner.timeOfDay));
        EditorGUILayout.LabelField("Sun elevation", positioner.currentElevation.ToString("F1") + "\u00B0");
        EditorGUILayout.LabelField("Sun azimuth (from N)", positioner.currentAzimuth.ToString("F1") + "\u00B0");

        EditorGUILayout.HelpBox(
            positioner.currentElevation > 0f
                ? "Sun is above the horizon."
                : "Sun is below the horizon (night).",
            MessageType.Info);
    }

    // converts a 0..24 decimal hour into a 12-hour clock string, e.g. 6.5 -> "6:30 AM"
    private static string FormatTime(float hours24)
    {
        int hour = Mathf.FloorToInt(hours24) % 24;
        int minute = Mathf.FloorToInt((hours24 - Mathf.Floor(hours24)) * 60f);

        string suffix = hour < 12 ? "AM" : "PM";
        int hour12 = hour % 12;
        if (hour12 == 0)
        {
            hour12 = 12;
        }

        return string.Format("{0}:{1:00} {2}", hour12, minute, suffix);
    }
}
