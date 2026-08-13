import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import 'daemon/credentials_store.dart';
import 'daemon/daemon_client.dart';
import 'daemon/tailnet_gateway.dart';
import 'pairing_screen.dart';
import 'rooms_screen.dart';
import 'theme/tokens.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // #1176: Mobile app-level unhandled exception guard.
  FlutterError.onError = (FlutterErrorDetails details) {
    FlutterError.presentError(details);
  };

  PlatformDispatcher.instance.onError = (Object error, StackTrace stack) {
    debugPrint('Unhandled app error: $error\n$stack');
    return true;
  };

  await TailnetGateway.init();
  runApp(const AerMobileApp());
}

class AerMobileApp extends StatelessWidget {
  const AerMobileApp({super.key});

  @override
  Widget build(BuildContext context) {
    // #456: the generated theme replaces the Flutter starter's deepPurple seed, which was never a
    // design decision — it is what `flutter create` writes. Supplying both brightnesses is the
    // whole of "system" support: ThemeMode.system resolves the OS preference itself, so the three
    // modes decision 0006 asks for need no code of ours.
    return MaterialApp(
      title: 'Baton',
      theme: aerTheme(Brightness.light),
      darkTheme: aerTheme(Brightness.dark),
      themeMode: ThemeMode.system,
      home: const _StartupRouter(),
    );
  }
}

/// Routes on stored credentials: unpaired → pairing; paired → the switcher (RoomsScreen), the phone's
/// front door (#337/#1044). Builds the daemon client here and hands it down.
class _StartupRouter extends StatefulWidget {
  const _StartupRouter();

  @override
  State<_StartupRouter> createState() => _StartupRouterState();
}

class _StartupRouterState extends State<_StartupRouter> {
  bool _loaded = false;
  DaemonClient? _client;

  @override
  void initState() {
    super.initState();
    CredentialsStore().load().then((credentials) {
      if (!mounted) return;
      setState(() {
        _client = credentials == null
            ? null
            : DaemonClient(
                host: credentials.host,
                token: credentials.token,
                tsnetRouted: credentials.tsnetRouted,
              );
        _loaded = true;
      });
    });
  }

  @override
  Widget build(BuildContext context) {
    if (!_loaded) {
      return const Scaffold(body: Center(child: CircularProgressIndicator()));
    }
    final client = _client;
    return client != null ? RoomsScreen(client: client) : const PairingScreen();
  }
}
