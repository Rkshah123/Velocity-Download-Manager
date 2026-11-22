package com.velocity.downloader

import com.velocity.downloader.core.Links
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LinksTest {

    @Test
    fun `a link pasted without its scheme still downloads`() {
        assertEquals("https://samplelib.com/mp4/sample-5s.mp4", Links.normalize("samplelib.com/mp4/sample-5s.mp4"))
        assertEquals("https://www.w3schools.com/html/mov_bbb.mp4", Links.normalize("www.w3schools.com/html/mov_bbb.mp4"))
        assertEquals("https://192.168.0.4:8130/file.bin", Links.normalize("192.168.0.4:8130/file.bin"))
    }

    @Test
    fun `the link is found inside shared text`() {
        assertEquals(
            "https://samplelib.com/mp4/sample-5s.mp4",
            Links.normalize("Check this video https://samplelib.com/mp4/sample-5s.mp4 it is short")
        )
        assertEquals("https://samplelib.com/a.mp3", Links.normalize("samplelib.com/a.mp3"))
    }

    @Test
    fun `quotes brackets and sentence punctuation are not part of the address`() {
        assertEquals("https://example.com/a.mp4", Links.normalize("\"https://example.com/a.mp4\""))
        assertEquals("https://example.com/a.mp4", Links.normalize("(https://example.com/a.mp4)"))
        assertEquals("https://example.com/a.mp4", Links.normalize("https://example.com/a.mp4."))
        assertEquals("https://example.com/watch?v=1", Links.normalize("<https://example.com/watch?v=1>"))
    }

    @Test
    fun `a query string and percent escapes survive`() {
        assertEquals(
            "https://cdn.example.com/f.mp4?token=a%2Fb&x=1",
            Links.normalize("https://cdn.example.com/f.mp4?token=a%2Fb&x=1")
        )
    }

    @Test
    fun `an encoded space stays and non latin characters are encoded`() {
        assertEquals("https://example.com/a%20b.mp4", Links.normalize("https://example.com/a%20b.mp4"))
        assertEquals("https://example.com/%E0%A4%AB.bin", Links.normalize("https://example.com/\u092b.bin"))
    }

    @Test
    fun `words after the link are not part of its address`() {
        assertEquals("https://example.com/a.mp4", Links.normalize("https://example.com/a.mp4 it is short"))
    }

    @Test
    fun `text without a link and other schemes are not treated as a download`() {
        assertNull(Links.normalize("hello world"))
        assertNull(Links.normalize("https://"))
        assertTrue(Links.hasUnsupportedScheme("magnet:?xt=urn:btih:abc"))
        assertTrue(Links.hasUnsupportedScheme("ftp://example.com/file.iso"))
        assertNull(Links.normalize("ftp://example.com/file.iso"))
    }
}
