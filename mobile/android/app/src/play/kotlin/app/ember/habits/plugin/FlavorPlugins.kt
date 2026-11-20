package app.ember.habits.plugin

import com.getcapacitor.Plugin

/** Plugins only this flavor registers: none for the Play build, which must not update itself. */
internal object FlavorPlugins {

    fun all(): List<Class<out Plugin>> = emptyList()
}
