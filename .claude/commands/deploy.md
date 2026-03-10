Build and deploy the Muxer Android app to the connected device.

## Build
```bash
cd /c/projects/muxer
dotnet build src/Muxer.Android -c Release -t:Install -p:AndroidSdkDirectory="C:\Users\loure\AppData\Local\Android\Sdk" -p:JavaSdkDirectory="C:\Program Files\Microsoft\jdk-17.0.18-hotspot" -f net9.0-android
```

If the above `-t:Install` doesn't find the device, build and install separately:

### Build only
```bash
cd /c/projects/muxer
dotnet build src/Muxer.Android -c Release -p:AndroidSdkDirectory="C:\Users\loure\AppData\Local\Android\Sdk" -p:JavaSdkDirectory="C:\Program Files\Microsoft\jdk-17.0.18-hotspot" -f net9.0-android
```

### Install via ADB
ADB is at `/c/Users/loure/AppData/Local/Android/Sdk/platform-tools/adb.exe`.
Device connects wirelessly at `192.168.0.50:5555`.

```bash
export PATH="$PATH:/c/Users/loure/AppData/Local/Android/Sdk/platform-tools"
adb connect 192.168.0.50:5555
adb -s 192.168.0.50:5555 install -r src/Muxer.Android/bin/Release/net9.0-android/com.muxer.android-Signed.apk
```

## Notes
- APK output: `src/Muxer.Android/bin/Release/net9.0-android/com.muxer.android-Signed.apk`
- Device IP may change; run `adb devices` to verify
- Server must be running on `192.168.0.65:5199` for the app to connect
