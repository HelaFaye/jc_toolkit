"""Where the app keeps its files, and opening / saving files on every platform.

Desktop: a file chooser; files the app writes by itself (IRcamera.png, traffic_log.txt) go in
the folder the app runs from, or in Documents/Joy-Con Toolkit for the packaged app.

Android: apps can't browse the phone's storage, so files are opened and exported with
Android's own file picker (the Storage Access Framework). Files the app writes go in its
folder, Android/data/org.jctool.jctool/files, which a computer sees over USB.
"""
import os
import sys

from kivy.clock import Clock

from ..hidio import is_android
from .widgets import choose_file, message

_data_dir = None


def data_dir():
    global _data_dir
    if _data_dir is None:
        if is_android():
            from jnius import autoclass
            activity = autoclass("org.kivy.android.PythonActivity").mActivity
            ext = activity.getExternalFilesDir(None)
            _data_dir = (ext or activity.getFilesDir()).getAbsolutePath()
        elif getattr(sys, "frozen", False):
            docs = os.path.join(os.path.expanduser("~"), "Documents")
            _data_dir = os.path.join(docs if os.path.isdir(docs) else os.path.expanduser("~"), "Joy-Con Toolkit")
        else:
            _data_dir = os.getcwd()
        os.makedirs(_data_dir, exist_ok=True)
    return _data_dir


def data_path(name):
    return os.path.join(data_dir(), name)


def open_file(title, on_chosen, filters=None):
    """Lets the user pick a file, then calls on_chosen(path) on the UI thread."""
    if is_android():
        _Saf.get().open(on_chosen)
    else:
        choose_file(title, on_chosen, filters=filters)


def save_file(title, name, on_chosen):
    """Picks where to save `name`: on_chosen(path). On Android: the app's folder (then
    export() can copy it elsewhere)."""
    if is_android():
        on_chosen(data_path(name))
    else:
        choose_file(title, on_chosen, save_name=name, path=data_dir() if getattr(sys, "frozen", False) else None)


def export(path, mime="application/octet-stream"):
    """Android: offers to save a copy of `path` anywhere (Downloads, Drive, ...)."""
    if is_android():
        _Saf.get().export(path, mime)


class _Saf:
    """Android's file picker. Results come back through onActivityResult."""
    OPEN, CREATE = 0x4A01, 0x4A02
    _instance = None

    @classmethod
    def get(cls):
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    def __init__(self):
        from android import activity as events
        from jnius import autoclass
        self.Intent = autoclass("android.content.Intent")
        self.activity = autoclass("org.kivy.android.PythonActivity").mActivity
        self.pending = {}
        events.bind(on_activity_result=self._result)

    def open(self, on_chosen):
        intent = self.Intent(self.Intent.ACTION_OPEN_DOCUMENT)
        intent.addCategory(self.Intent.CATEGORY_OPENABLE)
        intent.setType("*/*")
        self.pending[self.OPEN] = on_chosen
        self.activity.startActivityForResult(intent, self.OPEN)

    def export(self, path, mime):
        intent = self.Intent(self.Intent.ACTION_CREATE_DOCUMENT)
        intent.addCategory(self.Intent.CATEGORY_OPENABLE)
        intent.setType(mime)
        intent.putExtra(self.Intent.EXTRA_TITLE, os.path.basename(path))
        self.pending[self.CREATE] = path
        self.activity.startActivityForResult(intent, self.CREATE)

    def _display_name(self, resolver, uri):
        try:
            cursor = resolver.query(uri, None, None, None, None)
            try:
                if cursor.moveToFirst():
                    return cursor.getString(cursor.getColumnIndex("_display_name"))
            finally:
                cursor.close()
        except Exception:
            pass
        return "opened_file"

    def _fd(self, uri, mode):
        """A Python file object on a content URI (the fd is ours after detachFd)."""
        pfd = self.activity.getContentResolver().openFileDescriptor(uri, mode)
        return os.fdopen(pfd.detachFd(), "rb" if mode == "r" else "wb")

    def _result(self, request, result, intent):
        if request not in (self.OPEN, self.CREATE):
            return
        what = self.pending.pop(request, None)
        if result != -1 or intent is None or what is None:     # -1: RESULT_OK
            return
        uri = intent.getData()
        try:
            if request == self.OPEN:
                name = os.path.basename(self._display_name(self.activity.getContentResolver(), uri))
                target = os.path.join(self.activity.getCacheDir().getAbsolutePath(), name)
                with self._fd(uri, "r") as src, open(target, "wb") as dst:
                    dst.write(src.read())
                Clock.schedule_once(lambda dt: what(target), 0)
            else:
                with open(what, "rb") as src, self._fd(uri, "wt") as dst:
                    dst.write(src.read())
                Clock.schedule_once(lambda dt: message("Saved", "Saved a copy of %s." % os.path.basename(what)), 0)
        except Exception as e:
            err = str(e)
            Clock.schedule_once(lambda dt: message("File", "Failed: %s" % err), 0)
