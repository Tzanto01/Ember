package app.ember.habits.diagnostics

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Every launch event Ember can produce is in the spec's vocabulary and matches Lookout's event grammar. */
class LaunchEventsTest {

    @Test
    fun everyEventTheBuildersCanProduceIsInTheVocabulary() {
        val bools = listOf(true, false)
        val events = buildList {
            for (cold in bools) for (saved in bools) for (history in bools) for (source in LaunchSource.entries) {
                for (webview in listOf("140.0.7339.207", null, "140 (beta)", "x".repeat(41))) {
                    add(LaunchEvents.launch(cold, saved, source, history, webview))
                }
            }
            LaunchSource.entries.forEach { add(LaunchEvents.newIntent(it)) }
            listOf(0L, 820L, 999_999_999L, 1_000_000_000L, -5L).forEach { add(LaunchEvents.pageLoaded(it)) }
            for (crashed in listOf(true, false, null)) for (priority in listOf(0, 2, null, -1)) {
                RendererThen.entries.forEach { add(LaunchEvents.rendererGone(crashed, priority, it)) }
            }
            IgnoredRoute.entries.forEach { add(LaunchEvents.routeIgnored(it)) }
            for (finishing in bools) for (config in bools) add(LaunchEvents.destroy(finishing, config))
        }

        events.forEach { assertInVocabulary(it) }
    }

    @Test
    fun unknownValuesAreLeftOutAndKeysKeepTheirOrder() {
        assertEquals(
            "launch cold=true saved=false source=notification history=false",
            LaunchEvents.launch(cold = true, saved = false, source = LaunchSource.NOTIFICATION, history = false, webview = "1 2"),
        )
        assertEquals("renderer_gone then=finish", LaunchEvents.rendererGone(null, null, RendererThen.FINISH))
        assertEquals("page_loaded", LaunchEvents.pageLoaded(-1))
        assertEquals("route_ignored reason=rejected", LaunchEvents.routeIgnored(IgnoredRoute.MOMENT_OTHER_PACKAGE))
    }

    companion object {
        private val LOOKOUT = Regex("""^[a-z_]{1,40}( [a-z_]{1,20}=[\w.:-]{1,40}){0,8}$""")
        private const val BOOL = "(true|false)"
        private const val SOURCE = "(user|notification|system)"
        private const val INT = "[0-9]{1,9}"
        private val VOCABULARY = listOf(
            "launch( cold=$BOOL)?( saved=$BOOL)?( source=$SOURCE)?( history=$BOOL)?( webview=[0-9A-Za-z._-]{1,40})?",
            "new_intent( source=$SOURCE)?",
            "page_loaded( ms=$INT)?",
            "renderer_gone( crashed=$BOOL)?( priority=$INT)?( then=(recreate|finish|none))?",
            "route_ignored( reason=(restoring|no_bridge|from_history|rejected))?",
            "destroy( finishing=$BOOL)?( config_change=$BOOL)?",
        ).map(::Regex)

        /** Fails unless [event] is one of the spec's vocabulary events and matches Lookout's grammar. */
        fun assertInVocabulary(event: String) {
            assertTrue(event, LOOKOUT.matches(event))
            assertTrue(event, VOCABULARY.any { it.matches(event) })
        }
    }
}
