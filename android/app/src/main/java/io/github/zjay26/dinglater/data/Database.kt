package io.github.zjay26.dinglater.data

import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.RoomDatabase
import kotlinx.coroutines.flow.Flow

@Entity(
    tableName = "messages",
    indices = [
        Index(value = ["fingerprint"], unique = true),
        Index(value = ["state", "capturedAt"])
    ]
)
data class MessageEntity(
    @PrimaryKey val id: String,
    val fingerprint: String,
    val capturedAt: Long,
    val messageAt: Long?,
    val expiresAt: Long,
    val updatedAt: Long,
    val state: String,
    val snoozedUntil: Long?,
    val quality: String,
    val sourcePackage: String,
    val sourceIdentity: ByteArray,
    val dingTalkVersion: String,
    val conversationIsGroup: Boolean?,
    val conversation: ByteArray,
    val sender: ByteArray,
    val body: ByteArray
)

@Dao
interface MessageDao {
    @Query("SELECT * FROM messages ORDER BY capturedAt DESC")
    fun observeAll(): Flow<List<MessageEntity>>

    @Query("SELECT * FROM messages WHERE id = :id LIMIT 1")
    suspend fun get(id: String): MessageEntity?

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insert(entity: MessageEntity): Long

    @Query("UPDATE messages SET state = :state, snoozedUntil = :snoozedUntil, updatedAt = :updatedAt WHERE id = :id")
    suspend fun updateState(id: String, state: String, snoozedUntil: Long?, updatedAt: Long): Int

    @Query("UPDATE messages SET state = :nextState, snoozedUntil = NULL, updatedAt = :updatedAt WHERE state = :currentState")
    suspend fun updateAllByState(currentState: String, nextState: String, updatedAt: Long): Int

    @Query("DELETE FROM messages WHERE id = :id")
    suspend fun delete(id: String): Int

    @Query("DELETE FROM messages WHERE state = :state")
    suspend fun deleteByState(state: String): Int

    @Query("DELETE FROM messages")
    suspend fun deleteAll()

    @Query("DELETE FROM messages WHERE expiresAt <= :now")
    suspend fun deleteExpired(now: Long): Int

    @Query("UPDATE messages SET state = 'INBOX', snoozedUntil = NULL, updatedAt = :now WHERE state = 'SNOOZED' AND snoozedUntil <= :now")
    suspend fun releaseDue(now: Long): Int

    @Query("SELECT * FROM messages WHERE state = 'SNOOZED' AND snoozedUntil IS NOT NULL ORDER BY snoozedUntil")
    suspend fun listSnoozed(): List<MessageEntity>

    @Query("UPDATE messages SET expiresAt = capturedAt + :retentionMillis")
    suspend fun updateRetention(retentionMillis: Long)
}

@Database(entities = [MessageEntity::class], version = 1, exportSchema = true)
abstract class DingLaterDatabase : RoomDatabase() {
    abstract fun messages(): MessageDao
}
