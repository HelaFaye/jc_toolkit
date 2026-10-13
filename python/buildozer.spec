# Android build (buildozer / python-for-android). From this folder, on Linux or macOS:
#   .venv/bin/python scripts/build.py android       (or: buildozer android debug)
# The APK goes to bin/. The GitHub Actions workflow builds it too.

[app]
title = Joy-Con Toolkit
package.name = jctool
package.domain = org.jctool
source.dir = .
source.include_exts = py,png
source.exclude_dirs = tests,tools,scripts,packaging,build,dist,bin,.venv,.buildozer,.pytest_cache
version.regex = __version__ = ["'](.*)["']
version.filename = %(source.dir)s/jctool/__init__.py
# No hidapi or MIDI device libraries on Android: USB goes through jctool/android_usb.py
requirements = python3,kivy==2.3.1,pyjnius,android
orientation = landscape
fullscreen = 0
icon.filename = %(source.dir)s/packaging/icon.png

android.api = 34
android.minapi = 24
android.archs = arm64-v8a, armeabi-v7a
android.accept_sdk_license = True
android.features = android.hardware.usb.host
android.manifest.intent_filters = packaging/android/usb_intent_filter.xml
android.add_resources = packaging/android/device_filter.xml:xml/device_filter.xml
android.allow_backup = True

[buildozer]
log_level = 2
warn_on_root = 0
