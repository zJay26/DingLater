package io.github.zjay26.dinglater.data

import androidx.room.testing.MigrationTestHelper
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class MigrationSchemaInstrumentedTest {
    @get:Rule
    val helper = MigrationTestHelper(
        InstrumentationRegistry.getInstrumentation(),
        DingLaterDatabase::class.java
    )

    @Test
    fun versionOneSchemaCanBeCreatedAndValidated() {
        helper.createDatabase("migration-schema-test", 1).close()
        helper.runMigrationsAndValidate("migration-schema-test", 1, true)
    }
}
