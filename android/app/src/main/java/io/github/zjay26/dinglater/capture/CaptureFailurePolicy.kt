package io.github.zjay26.dinglater.capture

import io.github.zjay26.dinglater.security.LocalKeyUnavailableException
import java.util.Collections
import java.util.IdentityHashMap

internal object CaptureFailurePolicy {
    fun shouldStopCapture(exception: Throwable): Boolean = causes(exception)
        .any { it is LocalKeyUnavailableException }

    fun diagnosticKind(exception: Throwable): String = causes(exception)
        .firstOrNull()
        ?.javaClass
        ?.simpleName
        ?.takeIf(String::isNotBlank)
        ?.take(80)
        ?: "CaptureException"

    private fun causes(exception: Throwable): Sequence<Throwable> = sequence {
        val visited = Collections.newSetFromMap(IdentityHashMap<Throwable, Boolean>())
        var current: Throwable? = exception
        while (current != null && visited.add(current)) {
            yield(current)
            current = current.cause
        }
    }
}
