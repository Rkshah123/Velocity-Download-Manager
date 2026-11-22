package com.velocity.downloader.core

/**
 * A single token bucket shared by every transfer, so the Traffic limits speed applies to the whole app rather than to
 * each file separately. A limit of 0 lets bytes through untouched. Connections wait inside [acquire], which paces the
 * read loops without any timers of their own.
 */
object BandwidthLimiter {

    @Volatile
    var bytesPerSecond: Long = 0L

    private var nextSlotNanos = 0L

    fun acquire(bytes: Int) {
        val limit = bytesPerSecond
        if (limit <= 0L || bytes <= 0) return
        synchronized(this) {
            val now = System.nanoTime()
            if (nextSlotNanos < now) nextSlotNanos = now
            nextSlotNanos += bytes * 1_000_000_000L / limit
            val waitNanos = nextSlotNanos - now
            if (waitNanos > 1_000_000L) {
                runCatching { Thread.sleep(waitNanos / 1_000_000L) }
            }
        }
    }
}
