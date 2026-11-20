package app.ember.habits

import com.getcapacitor.Plugin

/** Plugins only the sideloaded beta build registers. */
internal object FlavorPlugins {

    fun all(): List<Class<out Plugin>> = listOf(EmberUpdaterPlugin::class.java)
}
