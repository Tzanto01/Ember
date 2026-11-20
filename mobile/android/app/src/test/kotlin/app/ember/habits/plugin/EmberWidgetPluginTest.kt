package app.ember.habits.plugin

import com.getcapacitor.PluginMethod
import com.getcapacitor.annotation.CapacitorPlugin
import java.lang.reflect.Modifier
import org.junit.Assert.assertEquals
import org.junit.Test

/** The web app reaches the widget plugin by its Capacitor name and method names (`web/src/lib/native/widget.ts`). */
class EmberWidgetPluginTest {

    @Test
    fun theWebAppFindsThePluginAndEveryMethodItCalls() {
        assertEquals("EmberWidget", EmberWidgetPlugin::class.java.getAnnotation(CapacitorPlugin::class.java)?.name)
        val exposed = EmberWidgetPlugin::class.java.declaredMethods
            .filter { it.isAnnotationPresent(PluginMethod::class.java) && Modifier.isPublic(it.modifiers) }
            .map { it.name }
            .toSet()
        val called = setOf(
            "update", "clear", "setKey", "hasKey", "takePending", "setLanguage", "setStyle",
            "canOpenScreenTime", "openScreenTime", "takeLaunchAction",
        )
        assertEquals(called, exposed)
    }
}
