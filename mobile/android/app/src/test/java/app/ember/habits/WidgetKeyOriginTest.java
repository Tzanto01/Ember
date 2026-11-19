package app.ember.habits;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;

import org.junit.Test;

public class WidgetKeyOriginTest {

    @Test
    public void acceptsHttpsOriginAndDropsTrailingSlash() {
        assertEquals("https://api.example.test", WidgetKey.origin("https://api.example.test"));
        assertEquals("https://api.example.test", WidgetKey.origin("https://api.example.test/"));
    }

    @Test
    public void keepsExplicitPort() {
        assertEquals("https://api.example.test:8443", WidgetKey.origin("https://api.example.test:8443"));
        assertEquals("https://api.example.test:8443", WidgetKey.origin("https://api.example.test:8443/"));
    }

    @Test
    public void rejectsPlainHttp() {
        assertNull(WidgetKey.origin("http://api.example.test"));
    }

    @Test
    public void rejectsUserInfo() {
        assertNull(WidgetKey.origin("https://user:secret@api.example.test"));
        assertNull(WidgetKey.origin("https://evil.example.test@api.example.test"));
    }

    @Test
    public void rejectsPath() {
        assertNull(WidgetKey.origin("https://api.example.test/api"));
        assertNull(WidgetKey.origin("https://api.example.test//"));
    }

    @Test
    public void rejectsQuery() {
        assertNull(WidgetKey.origin("https://api.example.test/?next=x"));
        assertNull(WidgetKey.origin("https://api.example.test?"));
    }

    @Test
    public void rejectsFragment() {
        assertNull(WidgetKey.origin("https://api.example.test/#x"));
        assertNull(WidgetKey.origin("https://api.example.test#"));
    }

    @Test
    public void rejectsEmptyMissingAndGarbage() {
        assertNull(WidgetKey.origin(null));
        assertNull(WidgetKey.origin(""));
        assertNull(WidgetKey.origin("https://"));
        assertNull(WidgetKey.origin("https:api.example.test"));
        assertNull(WidgetKey.origin("not a url"));
        assertNull(WidgetKey.origin("//api.example.test"));
    }
}
