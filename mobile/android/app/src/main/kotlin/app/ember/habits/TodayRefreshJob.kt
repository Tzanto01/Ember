package app.ember.habits

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.os.SystemClock
import app.ember.habits.widget.CheckInClient
import app.ember.habits.widget.DayClient
import app.ember.habits.widget.WidgetKey
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/**
 * Fetches the Today widget's day with the widget key ([DayClient]) once a network is up, then redraws the widget. One
 * job at a time, not kept across reboots, and one fetch at a time; a failed fetch waits for the widget's next trigger.
 */
class TodayRefreshJob : JobService() {

    override fun onStartJob(params: JobParameters): Boolean {
        run(applicationContext) { jobFinished(params, false) }
        return true
    }

    override fun onStopJob(params: JobParameters) = false

    companion object {
        private const val JOB_ID = 1
        private val FETCH: ExecutorService = Executors.newSingleThreadExecutor()
        private val waiting = ArrayList<Runnable>()
        private var fetching = false

        /** Schedules the fetch, replacing one still waiting. */
        internal fun schedule(context: Context) {
            val scheduler = context.getSystemService(JobScheduler::class.java) ?: return
            val job = JobInfo.Builder(JOB_ID, ComponentName(context, TodayRefreshJob::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .setPersisted(false)
                .build()
            scheduler.schedule(job)
        }

        /**
         * Runs the fetch on its own thread, apart from the widget's check-ins, with [CheckInClient.BUDGET_MS] from
         * its start, redraws the widget and then calls [finished]. Runs while a fetch is running share one more fetch
         * after it.
         */
        internal fun run(context: Context, finished: Runnable) {
            synchronized(waiting) {
                waiting.add(finished)
                if (fetching) return
                fetching = true
            }
            FETCH.execute { drain(context) }
        }

        private fun drain(context: Context) {
            while (true) {
                val finished = synchronized(waiting) {
                    if (waiting.isEmpty()) {
                        fetching = false
                        return
                    }
                    ArrayList(waiting).also { waiting.clear() }
                }
                try {
                    val key = WidgetKey.get(context)
                    val apiBase = WidgetKey.apiBase(context)
                    if (key != null && apiBase != null) {
                        DayClient.fetch(context, apiBase, key, SystemClock.elapsedRealtime() + CheckInClient.BUDGET_MS)
                    }
                    TodayWidget.refresh(context)
                } finally {
                    finished.forEach { it.run() }
                }
            }
        }
    }
}
