package app.ember.habits

import java.io.IOException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.Semaphore
import kotlin.concurrent.thread

/**
 * An https origin on this machine that takes connections and never answers them, so a request to it hangs until
 * [release]; from then on it drops every connection at once. [accepted] gains a permit per connection.
 */
class HangingOrigin : AutoCloseable {

    private val server = ServerSocket(0, 50, InetAddress.getLoopbackAddress())
    private val held = ArrayList<Socket>()
    private var released = false

    val accepted = Semaphore(0)
    val base = "https://127.0.0.1:${server.localPort}"

    init {
        thread(isDaemon = true) {
            while (true) {
                val socket = try {
                    server.accept()
                } catch (e: IOException) {
                    return@thread
                }
                synchronized(held) { if (released) socket.close() else held.add(socket) }
                accepted.release()
            }
        }
    }

    fun release() {
        synchronized(held) {
            released = true
            held.forEach { it.close() }
            held.clear()
        }
    }

    override fun close() {
        release()
        server.close()
    }
}
