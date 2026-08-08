package io.github.zjay26.dinglater.capture

import io.github.zjay26.dinglater.security.LocalKeyUnavailableException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CaptureFailurePolicyTest {
    @Test
    fun `transient notification failure does not stop later captures`() {
        val exception = IllegalArgumentException("malformed notification payload")

        assertFalse(CaptureFailurePolicy.shouldStopCapture(exception))
        assertEquals("IllegalArgumentException", CaptureFailurePolicy.diagnosticKind(exception))
    }

    @Test
    fun `local key failure stops capture even when wrapped`() {
        val exception = IllegalStateException(
            "repository failed",
            LocalKeyUnavailableException(IllegalStateException("keystore failed"))
        )

        assertTrue(CaptureFailurePolicy.shouldStopCapture(exception))
    }
}
