# BlinctureMTP

CLI to import photos from MTP devices (like Android), and organize the photos on disk by timestamp (photo taken)

- C# 7.0
- .NET Framework 4.6.1

## Usage

### System Info

Lists available devices.

````
> BlinctureMTP.exe systeminfo
$ # SystemInfo #
$ [Devices]
$ -       <device id>
$ [EXIT_CODE_OK]
````

### Device Info

Lists contents of a directory on device.

````
> BlinctureMTP.exe deviceinfo "<device id>" "/sdcard/DCIM/Camera"
$ # DeviceInfo #
$ [Directory]
$ Camera/ <id X>
$         image1.jpg <id Y>
$         image2.jpg <id Z>
$ [EXIT_CODE_OK]
````

### Transfer

Transfers files from device.

````
> BlinctureMTP.exe transfer "<device id>" "/sdcard/DCIM/Camera" C:\Photos
````

````
> BlinctureMTP.exe transfer --waitOnFail --dateTimePattern="yyyyMM\/yyyyMMdd\/HHmmss" "<device id>" "/sdcard/DCIM/Camera" C:\Photos
````

## License

The MIT License (MIT) (see LICENSE.txt)
