package dev.todotracker.core

import java.io.ByteArrayInputStream
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.Base64
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class PhoneLinkTest {
    private val fp = "IorsF8EWp_xuEr8j9alpHPfNeN0GZZs8cxAfNok_am8"
    private val link = "todotracker://pair?v=1&hosts=192.168.1.20:5318,100.101.102.103:5318&code=Ab3-x_9&fp=$fp&name=Shahab%E2%80%99s%20MacBook%20%27Pro%27"

    @Test
    fun A_pairing_link_from_the_computer_gives_its_addresses_code_fingerprint_and_name() {
        val parsed = PhoneLink.parse(link)!!
        assertEquals(listOf("192.168.1.20:5318", "100.101.102.103:5318"), parsed.hosts)
        assertEquals("Ab3-x_9", parsed.code)
        assertEquals(fp, parsed.fingerprint)
        assertEquals("Shahab’s MacBook 'Pro'", parsed.computer)
    }

    @Test
    fun A_pairing_link_is_not_a_task_link() {
        assertTrue(PhoneLink.isPairing(link))
        assertTrue(PhoneLink.isPairing("TODOTRACKER://pair"))
        assertFalse(PhoneLink.isPairing("todotracker://task/0b5e0a43-34a0-4f2c-9a3e-1a2b3c4d5e6f"))
        assertFalse(PhoneLink.isPairing("todotracker://pairing?v=1"))
        assertFalse(PhoneLink.isPairing("https://example.com/pair"))
        assertFalse(PhoneLink.isPairing(null))
    }

    @Test
    fun A_link_with_a_missing_or_wrong_part_is_refused() {
        val bad = listOf(
            link.replace("v=1", "v=2"),
            link.replace("&code=Ab3-x_9", ""),
            link.replace("hosts=192.168.1.20:5318,100.101.102.103:5318", "hosts="),
            link.replace("192.168.1.20:5318", "192.168.1.20"),
            link.replace("192.168.1.20:5318", "192.168.1.20:99999"),
            link.replace("192.168.1.20:5318", "user@evil:5318"),
            link.replace("192.168.1.20:5318", "evil.com/x:5318"),
            link.replace(fp, fp.dropLast(1)),
            link.replace(fp, "$fp="),
            link.replace(fp, fp.replace('_', '/')),
            "$link&code=other",
            "todotracker://task/x?v=1",
            "",
        )
        for (text in bad) assertNull(text, PhoneLink.parse(text))
    }

    @Test
    fun A_link_without_a_name_still_works() {
        assertEquals("your computer", PhoneLink.parse(link.substringBefore("&name="))!!.computer)
    }

    // A fixed certificate (ECDSA P-256, as the computer makes) and the fingerprint of its DER (openssl dgst -sha256).
    private val der = Base64.getDecoder().decode(
        "MIIBYzCCAQigAwIBAgIUeHivmYQ85/BMnEc04f0Xz4wlmQQwCgYIKoZIzj0EAwIwFzEVMBMGA1UEAwwMVG9kbyBUcmFja2VyMB4XDTI2MTAyOTA2NTU0OVoXDTM2MTAyNjA2NTU0OVowFzEVMBMGA1UEAwwMVG9kbyBUcmFja2VyMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAET/gyIwKNvxA8b6AWdb8SmqGqVTGADGXxRJ4b3tb5b2uvn2AJUhY2s8oLjOqrMQbjoFqyrs6/8EAq/tiAZ4QXI6MyMDAwHQYDVR0OBBYEFO5vMplr2az+5onKYVzCxn9u4LSYMA8GA1UdEwEB/wQFMAMBAf8wCgYIKoZIzj0EAwIDSQAwRgIhAJ7rKmtzOjmryqBCsQzmlugvmRD29Iryz8j+KcY7CPoHAiEAnSPy/A8qI89BpLMUtY6h6vlyXcjXmt08UWqQ8cfsMUs=",
    )

    private val certificate = CertificateFactory.getInstance("X.509").generateCertificate(ByteArrayInputStream(der)) as X509Certificate

    @Test
    fun The_fingerprint_is_the_base64url_SHA_256_of_the_certificate_DER() {
        assertEquals(fp, CertificatePin.fingerprint(der))
        assertTrue(PhoneLink.isFingerprint(CertificatePin.fingerprint(der)))
    }

    @Test
    fun Only_the_pinned_certificate_is_trusted() {
        CertificatePin.TrustManager(fp).checkServerTrusted(arrayOf(certificate), "ECDHE_ECDSA")
        val other = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
        assertThrows(CertificateException::class.java) { CertificatePin.TrustManager(other).checkServerTrusted(arrayOf(certificate), "ECDHE_ECDSA") }
        assertThrows(CertificateException::class.java) { CertificatePin.TrustManager(fp).checkServerTrusted(emptyArray(), "ECDHE_ECDSA") }
        assertThrows(CertificateException::class.java) { CertificatePin.TrustManager(fp).checkClientTrusted(arrayOf(certificate), "ECDHE_ECDSA") }
        assertTrue(CertificatePin.matches(certificate, fp))
        assertFalse(CertificatePin.matches(certificate, other))
        assertFalse(CertificatePin.matches(null, fp))
    }
}
