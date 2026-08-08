package io.github.zjay26.dinglater.security

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.Mac
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class LocalKeyUnavailableException(cause: Throwable) : IllegalStateException("本地加密密钥不可用", cause)

class SecretBox(private val keyAlias: String = ENCRYPTION_ALIAS) {
    private val keyStore by lazy(LazyThreadSafetyMode.SYNCHRONIZED) {
        KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    }

    fun encrypt(plaintext: String): ByteArray = protect {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, encryptionKey())
        val ciphertext = cipher.doFinal(plaintext.toByteArray(StandardCharsets.UTF_8))
        ByteBuffer.allocate(1 + cipher.iv.size + ciphertext.size)
            .put(FORMAT_VERSION)
            .put(cipher.iv)
            .put(ciphertext)
            .array()
    }

    fun decrypt(protectedBytes: ByteArray): String = protect {
        require(protectedBytes.size >= 1 + IV_BYTES + 16 && protectedBytes[0] == FORMAT_VERSION) {
            "不支持的本地密文格式"
        }
        val nonce = protectedBytes.copyOfRange(1, 1 + IV_BYTES)
        val ciphertext = protectedBytes.copyOfRange(1 + IV_BYTES, protectedBytes.size)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, encryptionKey(), GCMParameterSpec(128, nonce))
        String(cipher.doFinal(ciphertext), StandardCharsets.UTF_8)
    }

    fun reset() = protect { keyStore.deleteEntry(keyAlias) }

    @Synchronized
    private fun encryptionKey(): SecretKey {
        (keyStore.getKey(keyAlias, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(
                KeyGenParameterSpec.Builder(
                    keyAlias,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
                )
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build()
            )
            generateKey()
        }
    }

    private inline fun <T> protect(block: () -> T): T = try {
        block()
    } catch (exception: LocalKeyUnavailableException) {
        throw exception
    } catch (exception: Exception) {
        throw LocalKeyUnavailableException(exception)
    }

    companion object {
        private const val ENCRYPTION_ALIAS = "DingLater.LocalMessageKey.v1"
        private const val IV_BYTES = 12
        private const val FORMAT_VERSION: Byte = 1
    }
}

class FingerprintService(private val keyAlias: String = HMAC_ALIAS) {
    private val keyStore by lazy(LazyThreadSafetyMode.SYNCHRONIZED) {
        KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    }

    fun fingerprint(vararg parts: String): String = try {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(hmacKey())
        parts.forEach { part ->
            val bytes = part.toByteArray(StandardCharsets.UTF_8)
            mac.update(ByteBuffer.allocate(4).putInt(bytes.size).array())
            mac.update(bytes)
        }
        mac.doFinal().joinToString("") { "%02x".format(it) }
    } catch (exception: Exception) {
        throw LocalKeyUnavailableException(exception)
    }

    fun reset() = try {
        keyStore.deleteEntry(keyAlias)
    } catch (exception: Exception) {
        throw LocalKeyUnavailableException(exception)
    }

    @Synchronized
    private fun hmacKey(): SecretKey {
        (keyStore.getKey(keyAlias, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_HMAC_SHA256, "AndroidKeyStore").run {
            init(
                KeyGenParameterSpec.Builder(keyAlias, KeyProperties.PURPOSE_SIGN)
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .build()
            )
            generateKey()
        }
    }

    companion object {
        private const val HMAC_ALIAS = "DingLater.LocalFingerprintKey.v1"
    }
}
