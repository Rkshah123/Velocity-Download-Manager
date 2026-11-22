package com.velocity.downloader

import com.velocity.downloader.core.FileNames
import com.velocity.downloader.core.Formats
import com.velocity.downloader.core.Segment
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class FileNamesTest {

    @Test
    fun `plain filename is read from the disposition header`() {
        assertEquals("report.pdf", FileNames.fromDisposition("attachment; filename=\"report.pdf\""))
        assertEquals("archive.zip", FileNames.fromDisposition("attachment; filename=archive.zip"))
    }

    @Test
    fun `extended filename wins and is decoded`() {
        assertEquals("Trilogy part 1.mkv", FileNames.fromDisposition("attachment; filename=\"fallback.mkv\"; filename*=UTF-8''Trilogy%20part%201.mkv"))
    }

    @Test
    fun `name is taken from the url path and ignores the query string`() {
        assertEquals("movie.mp4", FileNames.fromUrl("https://cdn.example.com/media/movie.mp4?token=abc&expires=99"))
        assertEquals("doc.pdf", FileNames.fromUrl("https://example.com/get/doc.pdf/"))
        assertNull(FileNames.fromUrl("https://example.com/"))
    }

    @Test
    fun `missing extension is filled in from the content type only`() {
        assertEquals("stream.mp4", FileNames.ensureExtension("stream", "video/mp4"))
        assertEquals("setup.exe", FileNames.ensureExtension("setup.exe", "application/octet-stream"))
        assertEquals("no-extension", FileNames.ensureExtension("no-extension", "application/x-unknown-type"))
    }

    @Test
    fun `an existing name is never overwritten`() {
        assertEquals("video.mp4", FileNames.unique("video.mp4", emptySet()))
        assertEquals("video (1).mp4", FileNames.unique("video.mp4", setOf("video.mp4")))
        assertEquals("video (2).mp4", FileNames.unique("video.mp4", setOf("video.mp4", "video (1).mp4")))
    }

    @Test
    fun `unsafe characters are replaced and empty names get a fallback`() {
        assertEquals("a_b_c_d_.txt", FileNames.sanitize("a:b/c\\d?.txt"))
        assertEquals("download", FileNames.sanitize("   "))
    }

    @Test
    fun `formats stay readable`() {
        assertEquals("512 B", Formats.bytes(512))
        assertEquals("1.5 KB", Formats.bytes(1536))
        assertEquals("unknown", Formats.bytes(-1))
        assertEquals(10, Formats.eta(0, 100, 10))
    }

    @Test
    fun `segment length counts both ends`() {
        assertEquals(100L, Segment(0, 99).length)
        assertEquals(25L, Segment(50, 149, 25).written)
    }
}
