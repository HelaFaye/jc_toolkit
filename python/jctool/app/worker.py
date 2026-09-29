"""The device thread: every controller call of the app runs here, one at a time.

Long operations (IR stream, button test, SPI backup, NFC scan, HD Rumble) call Ui.poll()
often; poll() runs the jobs queued meanwhile, so a command from the window (battery, LEDs,
live IR settings...) runs in between reports, like on Windows where the original ran them
from its message loop. Results come back to the Kivy thread through Clock.
"""
import queue
import threading
import traceback

from kivy.clock import Clock


class Job:
    def __init__(self, fn, done, long_running, name):
        self.fn = fn
        self.done = done
        self.long_running = long_running
        self.name = name


class DeviceWorker:
    def __init__(self):
        self.jc = None
        self.jobs = queue.Queue()
        self.busy_long = None           # The name of the long operation running, or None
        self.thread = threading.Thread(target=self._run, name="device", daemon=True)
        self.thread.start()

    def run(self, fn, done=None, long_running=False, name=""):
        """Runs fn(jc) on the device thread; done(result) runs on the Kivy thread (also on an
        exception: done gets the exception)."""
        self.jobs.put(Job(fn, done, long_running, name))

    def _run(self):
        while True:
            job = self.jobs.get()
            if job is None:
                return
            self._execute(job)

    def _execute(self, job):
        if job.long_running:
            self.busy_long = job.name or "busy"
        try:
            result = job.fn(self.jc) if self.jc is not None or job.name == "connect" else None
        except Exception as e:           # Device gone etc.: report, don't kill the thread
            traceback.print_exc()
            result = e
        finally:
            if job.long_running:
                self.busy_long = None
        if job.done is not None:
            Clock.schedule_once(lambda dt, r=result: job.done(r), 0)

    def poll(self):
        """Called from inside long operations: runs short jobs queued meanwhile. Long ones
        wait until the running one ends."""
        deferred = []
        while True:
            try:
                job = self.jobs.get_nowait()
            except queue.Empty:
                break
            if job is None or job.long_running:
                deferred.append(job)
                continue
            self._execute(job)
        for job in deferred:
            self.jobs.put(job)

    def stop(self):
        self.jobs.put(None)
