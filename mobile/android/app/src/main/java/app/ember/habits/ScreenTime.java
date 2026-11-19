package app.ember.habits;

import android.content.Context;
import android.content.Intent;
import android.content.pm.ResolveInfo;
import java.util.Arrays;
import java.util.List;
import java.util.function.Predicate;

/**
 * Points to the system's own screen-time screen. Ember only opens it and never reads usage data: no usage-stats
 * permission, only {@code <queries>} for the candidates below.
 */
final class ScreenTime {

    /** In order of preference: {package, activity} of a screen-time app, or {action} of a public Settings screen. */
    static final List<String[]> CANDIDATES = Arrays.asList(
        new String[] { "com.google.android.apps.wellbeing", "com.google.android.apps.wellbeing.settings.TopLevelSettingsActivity" },
        new String[] { "com.samsung.android.forest", "com.samsung.android.forest.launcher.LauncherActivity" },
        new String[] { "android.settings.action.APP_USAGE_SETTINGS" }
    );

    private ScreenTime() {}

    /** The first of {@code candidates} that {@code opens}, or null. */
    static String[] first(List<String[]> candidates, Predicate<String[]> opens) {
        for (String[] candidate : candidates) if (opens.test(candidate)) return candidate;
        return null;
    }

    /** An intent for the first candidate with an exported activity on this device, or null. */
    static Intent intent(Context context) {
        String[] candidate = first(CANDIDATES, c -> {
            ResolveInfo info = context.getPackageManager().resolveActivity(intent(context, c), 0);
            return info != null && info.activityInfo != null && info.activityInfo.exported;
        });
        return candidate == null ? null : intent(context, candidate);
    }

    private static Intent intent(Context context, String[] candidate) {
        if (candidate.length == 2) return new Intent().setClassName(candidate[0], candidate[1]);
        return new Intent(candidate[0]).putExtra(Intent.EXTRA_PACKAGE_NAME, context.getPackageName());
    }
}
