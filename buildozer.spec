# Android build (buildozer / python-for-android). From this folder, on Linux or macOS:
#   .venv/bin/python scripts/build.py android       (or: buildozer android debug)
# The APK goes to bin/. The GitHub Actions workflow builds it too.

[app]
title = Joy-Con Toolkit
package.name = jctool
package.domain = org.jctool
source.dir = .
source.include_exts = py,png
source.exclude_dirs = tests,tools,scripts,packaging,native,build,dist,bin,.venv,.venv-android,.buildozer,.pytest_cache,.github
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
# uses-feature in the manifest (python-for-android develop dropped --feature, which android.features used)
android.extra_manifest_xml = packaging/android/extra_manifest.xml
android.manifest.intent_filters = packaging/android/usb_intent_filter.xml
android.add_resources = packaging/android/device_filter.xml:xml/device_filter.xml
android.allow_backup = True

# python-for-android: develop, pinned. master installs the pure-Python dependencies it
# resolves (Kivy's: requests, charset-normalizer...) without the Android platform tags,
# and pip refuses charset-normalizer's Android wheel ("not a supported wheel").
p4a.branch = develop
p4a.commit = f644456401e90d2b9083481a6126e46b85c5ef36

[buildozer]
log_level = 2
warn_on_root = 0
