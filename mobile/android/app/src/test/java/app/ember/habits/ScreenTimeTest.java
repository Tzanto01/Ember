package app.ember.habits;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;

import java.util.Arrays;
import java.util.HashSet;
import java.util.Set;
import org.junit.Test;

public class ScreenTimeTest {

    private static String[] pick(String... installed) {
        Set<String> found = new HashSet<>(Arrays.asList(installed));
        return ScreenTime.first(ScreenTime.CANDIDATES, candidate -> found.contains(candidate[0]));
    }

    @Test
    public void prefersGoogleDigitalWellbeing() {
        String[] picked = pick("com.samsung.android.forest", "com.google.android.apps.wellbeing");
        assertEquals("com.google.android.apps.wellbeing", picked[0]);
    }

    @Test
    public void fallsBackToSamsung() {
        assertEquals("com.samsung.android.forest", pick("com.samsung.android.forest")[0]);
    }

    @Test
    public void nothingWhenNoCandidateOpens() {
        assertNull(pick());
    }
}
